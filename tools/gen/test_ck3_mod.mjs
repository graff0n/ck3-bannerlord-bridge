#!/usr/bin/env node
// test_ck3_mod.mjs - headless self-tests for the CK3 mod "ck3xbl" and its tools (no game launch needed).
//
//  keypad.*  Runs the GENERATED keypad script (scripted GUIs -> ck3xbl_keypad_*_effect, ck3xbl_commit_effect) in a
//            small interpreter for the script subset it uses, fed with the key sequences and run-file bodies of the
//            reference encoder (keypad_payload.mjs). Proves that the encoder and the mod agree on the checksum for
//            payload versions 3 and 2, that corrupted sequences (a repeated or lost store key, a wrong bid) are
//            rejected, and that every intermediate value stays inside CK3's fixed-point range.
//  knights.* The knights and commanders records (PROTOCOL 13.2): every record family of the emit plan is
//            generated, knight effectiveness is the draft's effect plus the clearing of its helper scopes, saved before and
//            cleared after every A/J dump, every helper scope is cleared in the block that saved it, and the leveled-trait
//            chains (with a track, with 4 levels) lint clean.
//  award.*   The award slots (PROTOCOL 13.5): the reference vectors commit in the interpreted keypad, the interpreted decode
//            of the award value, and the place of the award calls in ck3xbl_apply_effect.
//  snapshot.* The C records (PROTOCOL 15): ck3xbl_c_absent on every C dump, run in the interpreter, and the C role order.
//  gen.*     Every generator (gen_ck3_mod.mjs and tools/gen/knights) is up to date (--check).
//  steer.*   The generated battle clock files (PROTOCOL 14) against tools/gen/lib/steering.mjs:
//            the day markers and the interpreted ck3xbl_cday_marks_value, the lever modifiers and their interpreted scale
//            values, the hard modifier, the levers-off effect and the phase-event shield.
//  clock.*   The battle clock (PROTOCOL 14): payload 4 commits with ck3xbl_in_maxver and r0 = 5
//            is refused; the apply steers payload 4 and applies payload 3; the timeline, gain and owner-lever values; and,
//            run by lib/scriptsim.mjs (the mod's own effects, triggers and values against a toy world), the steering start,
//            the daily pulse in a toy combat (every controller case reaches its targets), joins and the second-clash offer, the end
//            call and its backstop, slot storage, the steering-start state and the fix. The toy combat gives each side the
//            dead share of its commanders' hard modifiers (routed men leave the fight), so the live clash 2 of 2026-10-04 (a
//            leaderless loser) is replayed with and without the hard cover; pulse dumps carry only clock keys; the 0002/0004
//            split of "The dust settles..." is pinned.
//  lint.*    Applies known mistakes to in-memory copies of mod files and checks that lint_ck3_mod.mjs reports each
//            of them (so a clean lint means something), and that the unmodified mod lints clean.
//
// Usage: node tools/gen/test_ck3_mod.mjs [filter]   (exit code 1 when a test fails)

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseText, stripBom, walk } from './lib/pdx.mjs';
import { AWARD_SLOTS, REG, awardSlotRegisters, payloadChecksum } from './lib/registers.mjs';
import { knightEffEffectLines } from './knights/knight_eff_emit.mjs';
import { ScriptSim, army, character, combat, joinCombat } from './lib/scriptsim.mjs';
import {
  DAMPING, DAY_MARKERS, DMG_DOWN_MAX, DMG_UP_MAX, END_BACKSTOP_DAYS, END_EFFECT, FAILSAFE_DAYS, FIRST_DAMAGE_DAY, G_MAX, G_MIN, HARD, HARD_END_DAY,
  KNIGHT_DOWN_MAX, KNIGHT_LEVER, KNIGHT_UP_MAX, LEVER_STEP, LEVERS, MALUS_FLOOR, PACE_DEFAULT, PACE_MAX, PACE_MIN, SHIELD_ENTRIES,
  SHIELD_WEIGHT, STEP_MAX, STEP_MIN, TOUGH_DOWN_MAX, HARD_COVER,
} from './lib/steering.mjs';
import { encode } from './keypad_payload.mjs';
import { DEFAULT_MOD, lintMod } from './lint_ck3_mod.mjs';
import { savedScopeNames } from './gen_ck3_mod.mjs';

const MOD = DEFAULT_MOD;
const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..');
// CK3 stores script values as fixed point; the keypad keeps every value far below this bound.
const FIXED_POINT_LIMIT = 2_000_000;

// ---------------------------------------------------------------------------------------------------------------
// A minimal interpreter for the keypad script (variables on one character, saved scope values, global variables)
// ---------------------------------------------------------------------------------------------------------------

class KeypadSim {
  constructor() {
    const read = (rel) => parseText(stripBom(fs.readFileSync(path.join(MOD, ...rel.split('/')), 'utf8')));
    this.effects = new Map();
    for (const n of read('common/scripted_effects/ck3xbl_gen_keypad_effects.txt')) this.effects.set(n.key, n.block);
    this.guis = new Map();
    for (const n of read('common/scripted_guis/ck3xbl_gen_keypad_sguis.txt')) this.guis.set(n.key, n.block.find((c) => c.key === 'effect').block);
    this.vars = new Map();
    this.globals = new Map();
    this.log = [];
    this.maxValue = 0;
  }

  press(gui) {
    this.saved = new Map();
    this.run(this.guis.get(gui), {});
  }

  /** The run-file body of the reference encoder (hidden_effect = { ... } with commit and apply). */
  runFile(text) {
    this.saved = new Map();
    const body = parseText(text)[0].block.filter((n) => n.key !== 'ck3xbl_apply_effect');
    this.run(body, {});
  }

  track(v) {
    if (!Number.isFinite(v)) throw new Error(`non-numeric value ${v}`);
    this.maxValue = Math.max(this.maxValue, Math.abs(v));
    if (Math.abs(v) >= FIXED_POINT_LIMIT) throw new Error(`value ${v} leaves the fixed-point range`);
    return v;
  }

  sub(s, params) {
    return typeof s === 'string' ? s.replace(/\$([A-Za-z0-9_]+)\$/g, (m, p) => (p in params ? params[p] : m)) : s;
  }

  ref(expr, params) {
    const e = this.sub(expr, params);
    if (/^-?\d+(\.\d+)?$/.test(e)) return Number(e);
    let m;
    if ((m = /^var:(.+)$/.exec(e))) return this.get(this.vars, m[1], e);
    if ((m = /^global_var:(.+)$/.exec(e))) return this.get(this.globals, m[1], e);
    if ((m = /^scope:(.+)$/.exec(e))) return this.get(this.saved, m[1], e);
    throw new Error(`unsupported reference ${e}`);
  }

  get(map, name, expr) {
    if (!map.has(name)) throw new Error(`${expr} is not set`);
    return map.get(name);
  }

  /** A value: a reference, or a script-value block whose operations (and if/else chains) run in order. */
  value(node, params) {
    if (!node.block) return this.ref(node.value, params);
    let v = 0;
    const run = (nodes) => {
      for (let i = 0; i < nodes.length; i++) {
        const n = nodes[i];
        if (n.key === 'if') {
          const chain = [n];
          while (nodes[i + 1] && (nodes[i + 1].key === 'else_if' || nodes[i + 1].key === 'else')) chain.push(nodes[++i]);
          const hit = this.pick(chain, params);
          if (hit) run(hit.block.filter((x) => x.key !== 'limit'));
          continue;
        }
        if (n.key === 'floor') {
          v = Math.floor(v);
          continue;
        }
        const x = this.value(n, params);
        if (n.key === 'value') v = x;
        else if (n.key === 'add') v += x;
        else if (n.key === 'subtract') v -= x;
        else if (n.key === 'multiply') v *= x;
        else if (n.key === 'divide') v /= x;
        else if (n.key === 'modulo') v %= x;
        else throw new Error(`unsupported value operation ${n.key}`);
        this.track(v);
      }
    };
    run(node.block);
    return v;
  }

  /** The branch of an if/else_if/else chain that runs, or undefined. */
  pick(chain, params) {
    return chain.find((c) => c.key === 'else' || this.test(c.block.find((x) => x.key === 'limit').block, params));
  }

  test(nodes, params) {
    return nodes.every((n) => {
      if (n.key === 'NOT') return !this.test(n.block, params);
      if (n.key === 'OR') return n.block.some((c) => this.test([c], params));
      if (n.key === 'AND') return this.test(n.block, params);
      if (n.key === 'has_variable') return this.vars.has(this.sub(n.value, params));
      if (n.key === 'has_global_variable') return this.globals.has(this.sub(n.value, params));
      const a = this.ref(n.key, params);
      const b = this.ref(n.value, params);
      switch (n.op) {
        case '=': return a === b;
        case '<': return a < b;
        case '>': return a > b;
        case '<=': return a <= b;
        case '>=': return a >= b;
        default: throw new Error(`unsupported comparison ${n.op}`);
      }
    });
  }

  run(nodes, params) {
    let i = 0;
    while (i < nodes.length) {
      const n = nodes[i];
      const k = this.sub(n.key, params);
      if (k === 'if') {
        const chain = [n];
        while (nodes[i + 1] && (nodes[i + 1].key === 'else_if' || nodes[i + 1].key === 'else')) chain.push(nodes[++i]);
        const hit = this.pick(chain, params);
        if (hit) this.run(hit.block.filter((x) => x.key !== 'limit'), params);
      } else if (k === 'set_variable' || k === 'change_variable' || k === 'save_scope_value_as') {
        const name = this.sub(n.block.find((c) => c.key === 'name').value, params);
        const map = k === 'save_scope_value_as' ? this.saved : this.vars;
        if (k === 'change_variable') map.set(name, this.track(this.get(map, name, name) + this.value(n.block.find((c) => c.key === 'add'), params)));
        else map.set(name, this.track(this.value(n.block.find((c) => c.key === 'value'), params)));
      } else if (k === 'remove_variable') {
        this.vars.delete(n.value);
      } else if (k === 'info_log') {
        this.log.push(n.value);
      } else if (k === 'info_log_scopes') {
        // dump: nothing to simulate
      } else if (this.effects.has(k)) {
        const p = {};
        for (const c of n.block || []) p[c.key] = this.sub(c.value, params);
        this.run(this.effects.get(k), p);
      } else {
        throw new Error(`unsupported effect ${k}`);
      }
      i++;
    }
  }
}

const KEY_GUI = { F1: 'ck3xbl_key_store', F2: 'ck3xbl_key_commit', F3: 'ck3xbl_key_reset', F5: 'ck3xbl_key_seek' };
const guiOf = (key) => KEY_GUI[key] || `ck3xbl_key_${key}`;

/** A fresh player after CHOICE|FIELD for `bid`: registers reset, ready 0. */
function freshSim(bid) {
  const sim = new KeypadSim();
  sim.globals.set('ck3xbl_pending_bid', bid);
  sim.saved = new Map();
  sim.run(sim.effects.get('ck3xbl_keypad_reset_effect'), {});
  sim.vars.set('ck3xbl_ready', 0);
  return sim;
}

function typeKeys(sim, keys) {
  for (const k of keys) sim.press(guiOf(k));
  return sim;
}

/** A realistic payload: header, `armies` army slots with MaA pairs, `chars` character slots. */
function samplePayload(rand, { bid = 7, armies = 3, chars = 6 } = {}) {
  const values = { [REG.bid]: bid, [REG.outcome]: 1 + Math.floor(rand() * 4), [REG.province]: Math.floor(rand() * 9000) };
  values[REG.summary.myLost] = Math.floor(rand() * 5000);
  values[REG.summary.enemyLost] = Math.floor(rand() * 5000);
  values[REG.summary.myKills] = Math.floor(rand() * 40);
  for (let i = 0; i < armies; i++) {
    const b = REG.army.base + REG.army.stride * i;
    values[b] = 1 + Math.floor(rand() * 900);
    values[b + 1] = Math.floor(rand() * 1001);
    for (let k = 0; k < 1 + Math.floor(rand() * REG.army.maaPairs); k++) {
      values[b + 2 + 2 * k] = 1 + Math.floor(rand() * 111);
      values[b + 3 + 2 * k] = Math.floor(rand() * 1001);
    }
  }
  for (let j = 0; j < chars; j++) {
    const b = REG.character.base + REG.character.stride * j;
    values[b] = 1 + Math.floor(rand() * 500);
    values[b + 1] = Math.floor(rand() * 5);
    values[b + 2] = rand() < 0.5 ? 1 + Math.floor(rand() * 500) : 0;
  }
  for (const k of Object.keys(values)) if (values[k] === 0) delete values[k];
  return values;
}

function rng(seed) {
  let s = seed >>> 0;
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
    return s / 2 ** 32;
  };
}

const lastRecord = (sim) => sim.log.filter((l) => l.startsWith('CK3XBL|IN|')).pop();

// ---------------------------------------------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------------------------------------------

const tests = [];
const test = (name, fn) => tests.push({ name, fn });
const assert = (cond, message) => {
  if (!cond) throw new Error(message);
};

test('keypad.v3_sequences_commit_ok', () => {
  const rand = rng(1);
  for (let t = 0; t < 25; t++) {
    const values = samplePayload(rand, { armies: 1 + (t % 8), chars: t % 21 });
    const enc = encode(values);
    const sim = typeKeys(freshSim(values[REG.bid]), enc.keys);
    assert(lastRecord(sim) === 'CK3XBL|IN|OK', `payload ${t}: expected IN|OK, got ${lastRecord(sim)}`);
    assert(sim.vars.get('ck3xbl_ready') === 1, `payload ${t}: ready not set`);
    for (let i = 0; i < REG.count; i++) {
      assert(sim.vars.get(`ck3xbl_r${i}`) === enc.registers[i], `payload ${t}: r${i} = ${sim.vars.get(`ck3xbl_r${i}`)}, sent ${enc.registers[i]}`);
    }
    assert(sim.saved.get('ck3xbl_in_sum') === enc.checksum, `payload ${t}: mod checksum ${sim.saved.get('ck3xbl_in_sum')} != ${enc.checksum}`);
  }
});

test('keypad.v2_legacy_payload_ok', () => {
  const rand = rng(2);
  for (let t = 0; t < 10; t++) {
    const values = { ...samplePayload(rand), [REG.version]: REG.legacyPayloadVersion };
    const enc = encode(values);
    assert(enc.checksum === payloadChecksum(enc.registers, 2), 'encoder did not use the plain sum for version 2');
    const sim = typeKeys(freshSim(values[REG.bid]), enc.keys);
    assert(lastRecord(sim) === 'CK3XBL|IN|OK', `legacy payload ${t}: expected IN|OK, got ${lastRecord(sim)}`);
  }
});

/** Registers r0..r199 as the simulated mod holds them. */
const registersOf = (sim) => [...Array(REG.count).keys()].map((i) => sim.vars.get(`ck3xbl_r${i}`));

test('keypad.repeated_store_key_never_accepts_wrong_data', () => {
  // Every F1 of every payload is repeated once. Before a seek this stores 0 into a register that is 0 anyway (the
  // data is unchanged and may be accepted); inside a run of consecutive registers it shifts the rest of the run by
  // one slot, which the position-weighted checksum of version 3 must reject (a plain sum cannot).
  const rand = rng(3);
  let shifted = 0;
  for (let t = 0; t < 12; t++) {
    const enc = encode(samplePayload(rand, { armies: 1 + (t % 4), chars: 2 + t }));
    enc.keys.forEach((k, at) => {
      if (k !== 'F1') return;
      const keys = [...enc.keys.slice(0, at + 1), 'F1', ...enc.keys.slice(at + 1)];
      const sim = typeKeys(freshSim(enc.registers[REG.bid]), keys);
      const same = registersOf(sim).every((v, i) => v === enc.registers[i]);
      if (!same) {
        shifted++;
        assert(lastRecord(sim) === 'CK3XBL|IN|BAD', `payload ${t}: repeated F1 at key ${at} shifted registers and was accepted`);
      }
    });
  }
  assert(shifted > 100, `only ${shifted} shifting cases were exercised`);
});

test('keypad.legacy_checksum_misses_shifts', () => {
  // Documents why version 3 exists: the same corruption passes the version 2 plain sum.
  const values = { [REG.version]: REG.legacyPayloadVersion, 1: 7, 2: 1, 3: 812, 4: 55, 5: 66 };
  const enc = encode(values);
  const at = enc.keys.indexOf('F1', enc.keys.indexOf('F1') + 3);
  const keys = [...enc.keys.slice(0, at + 1), 'F1', ...enc.keys.slice(at + 1)];
  const sim = typeKeys(freshSim(7), keys);
  assert(!registersOf(sim).every((v, i) => v === enc.registers[i]), 'the corruption did not change the data');
  assert(lastRecord(sim) === 'CK3XBL|IN|OK', 'expected the plain sum to miss the shift');
});

test('keypad.lost_store_key_rejected', () => {
  const rand = rng(4);
  for (let t = 0; t < 12; t++) {
    const enc = encode(samplePayload(rand));
    enc.keys.forEach((k, at) => {
      if (k !== 'F1') return;
      const sim = typeKeys(freshSim(enc.registers[REG.bid]), enc.keys.filter((_, i) => i !== at));
      assert(lastRecord(sim) === 'CK3XBL|IN|BAD', `payload ${t}: losing the store key at ${at} was accepted`);
    });
  }
});

test('keypad.seventh_digit_marks_overflow', () => {
  const enc = encode({ 1: 7, 2: 1, 4: 999999, 5: 999999 });
  // Losing the F1 after r4 merges 999999 and 999999 into a 12-digit value.
  const at = enc.keys.indexOf('F1', enc.keys.indexOf('9'));
  const sim = typeKeys(freshSim(7), enc.keys.filter((_, i) => i !== at));
  assert(sim.vars.get('ck3xbl_overflow') === 1, 'overflow not flagged');
  assert(lastRecord(sim) === 'CK3XBL|IN|BAD', 'an overflowing payload was accepted');
  typeKeys(sim, ['F3']);
  assert(sim.vars.get('ck3xbl_overflow') === 0, 'F3 did not clear the overflow flag');
});

test('keypad.wrong_bid_rejected', () => {
  const enc = encode(samplePayload(rng(5), { bid: 7 }));
  const sim = typeKeys(freshSim(8), enc.keys);
  assert(lastRecord(sim) === 'CK3XBL|IN|BAD', 'a payload for another battle was accepted');
});

test('keypad.run_file_resets_leftovers', () => {
  const rand = rng(6);
  const enc = encode(samplePayload(rand));
  const sim = freshSim(enc.registers[REG.bid]);
  // An aborted keypad burst left garbage in some registers.
  typeKeys(sim, ['4', '2', 'F1', '9', '9', 'F1', '1', '5', '0', 'F5', '7', 'F1']);
  sim.runFile(enc.runFile);
  assert(lastRecord(sim) === 'CK3XBL|IN|OK', `run file after leftovers: ${lastRecord(sim)}`);
});

test('keypad.commit_twice_is_harmless', () => {
  const enc = encode(samplePayload(rng(7)));
  const sim = typeKeys(freshSim(enc.registers[REG.bid]), [...enc.keys, 'F2']);
  assert(lastRecord(sim) === 'CK3XBL|IN|OK' && sim.vars.get('ck3xbl_ready') === 1, 'second commit changed the result');
});

test('keypad.reset_withdraws_commit', () => {
  const enc = encode(samplePayload(rng(8)));
  const sim = typeKeys(freshSim(enc.registers[REG.bid]), [...enc.keys, 'F3']);
  assert(sim.vars.get('ck3xbl_ready') === 0, 'F3 did not withdraw the committed payload');
  sim.vars.set('ck3xbl_ready', 2);
  typeKeys(sim, ['F3']);
  assert(sim.vars.get('ck3xbl_ready') === 2, 'F3 touched an applied payload (ready = 2)');
});

test('keypad.max_values_in_fixed_point_range', () => {
  const values = {};
  for (let i = 1; i < REG.checksum; i++) values[i] = 999999;
  const enc = encode(values);
  const sim = typeKeys(freshSim(999999), enc.keys);
  assert(lastRecord(sim) === 'CK3XBL|IN|OK', `all-max payload: ${lastRecord(sim)}`);
  assert(sim.maxValue < 1_010_000, `intermediate value ${sim.maxValue} too large`);
});

// --- battle clock: generated steering files ---------------------------------------------------------------------

const STEER_MODIFIERS = 'common/modifiers/ck3xbl_gen_steer_modifiers.txt';
const STEER_VALUES = 'common/script_values/ck3xbl_gen_steer_values.txt';
const STEER_EFFECTS = 'common/scripted_effects/ck3xbl_gen_steer_slot_effects.txt';
const STEER_CONTROL = 'common/scripted_effects/ck3xbl_gen_steer_control_effects.txt';
const SHIELD = 'common/combat_phase_events/ck3xbl_gen_shield_phase_events.txt';
const STEER_LOC = 'localization/english/ck3xbl_gen_steer_l_english.yml';
const STEER_LOC_FR = 'localization/french/ck3xbl_gen_steer_l_french.yml';

/** Top-level definitions of a mod file: name -> node. */
function defsOf(rel) {
  return new Map(parseText(stripBom(fs.readFileSync(path.join(MOD, ...rel.split('/')), 'utf8'))).map((n) => [n.key, n]));
}
const field = (node, key) => node.block.find((c) => c.key === key);

/** A script value of the mod evaluated by the interpreter on a scope holding `vars`. */
function evalScriptValue(node, vars) {
  const sim = new KeypadSim();
  sim.vars = new Map(Object.entries(vars));
  return sim.value(node, {});
}

test('steer.day_markers_count_expired_days', () => {
  const sets = defsOf(STEER_EFFECTS).get('ck3xbl_set_day_markers_effect').block.filter((n) => n.key === 'set_variable');
  assert(sets.length === DAY_MARKERS, `${sets.length} markers, expected ${DAY_MARKERS}`);
  const days = new Map(sets.map((n) => [field(n, 'name').value, Number(field(n, 'days').value)]));
  [...days.values()].sort((a, b) => a - b).forEach((d, i) => assert(d === i + 1, `marker days ${[...days.values()]} are not 1..${DAY_MARKERS}`));
  const value = defsOf(STEER_VALUES).get('ck3xbl_cday_marks_value');
  for (let day = 0; day <= DAY_MARKERS + 1; day++) {
    const alive = Object.fromEntries([...days].filter(([, d]) => d > day).map(([name]) => [name, 1]));
    const c = evalScriptValue(value, alive);
    assert(c === Math.min(day, DAY_MARKERS), `day ${day}: ck3xbl_cday_marks_value = ${c}`);
  }
});

test('steer.lever_modifiers_scale_by_their_variable', () => {
  const modifiers = defsOf(STEER_MODIFIERS);
  const values = defsOf(STEER_VALUES);
  const leversOff = defsOf(STEER_CONTROL).get('ck3xbl_st_owner_levers_off_effect').block;
  const loc = fs.readFileSync(path.join(MOD, ...STEER_LOC.split('/')), 'utf8');
  const locFr = fs.readFileSync(path.join(MOD, ...STEER_LOC_FR.split('/')), 'utf8');
  assert(stripBom(locFr).startsWith('l_french:\n'), `${STEER_LOC_FR} does not start with l_french:`);
  for (const lever of LEVERS) {
    for (const [dir, sign] of [['up', 1], ['down', -1]]) {
      const name = `ck3xbl_st_${lever.key}_${dir}`;
      const m = modifiers.get(name);
      assert(m, `modifier ${name} missing`);
      assert(Number(field(m, lever.modifier)?.value) === sign * LEVER_STEP, `${name}: ${lever.modifier} is not ${sign * LEVER_STEP}`);
      const scale = field(field(m, 'scale'), 'value').value;
      assert(values.has(scale), `${name}: scale value ${scale} is not generated`);
      assert(evalScriptValue(values.get(scale), { [name]: 120 }) === 120, `${scale} does not read ${name}`);
      assert(evalScriptValue(values.get(scale), {}) === 0, `${scale} is not 0 without ${name}`);
      assert(leversOff.some((n) => n.key === 'remove_variable' && n.value === name), `the levers-off effect keeps the variable ${name}`);
      assert(leversOff.some((n) => n.key === 'remove_character_modifier' && n.value === name), `the levers-off effect keeps ${name}`);
      assert(loc.includes(` ${name}:0 "${lever[dir]}"`), `loc name of ${name} is not "${lever[dir]}"`);
      const fr = new RegExp(`^ ${name}:0 "([^"]+)"$`, 'm').exec(locFr);
      assert(fr && fr[1] !== lever[dir], `${name} has no French name in ${STEER_LOC_FR}`);
      assert(locFr.includes(` ${name}_desc:0 "`), `${name}_desc has no French localization`);
    }
  }
  const hard = modifiers.get('ck3xbl_st_hard');
  for (const tag of ['hard_casualty_modifier', 'enemy_hard_casualty_modifier']) {
    assert(Number(field(hard, tag).value) === HARD, `ck3xbl_st_hard: ${tag} is not ${HARD}`);
  }
  const cover = modifiers.get('ck3xbl_st_hard_cover');
  assert(!!cover === HARD_COVER, `ck3xbl_st_hard_cover is ${cover ? '' : 'not '}generated with HARD_COVER ${HARD_COVER}`);
  if (cover) {
    for (const tag of ['hard_casualty_modifier', 'enemy_hard_casualty_modifier']) {
      assert(Number(field(cover, tag).value) === HARD, `ck3xbl_st_hard_cover: ${tag} is not ${HARD}`);
    }
    assert(loc.includes(' ck3xbl_st_hard_cover:0 ') && loc.includes(' ck3xbl_st_hard_cover_desc:0 '), 'ck3xbl_st_hard_cover has no localization');
    assert(locFr.includes(' ck3xbl_st_hard_cover:0 ') && locFr.includes(' ck3xbl_st_hard_cover_desc:0 '), 'ck3xbl_st_hard_cover has no French localization');
  }
  const expected = 2 * LEVERS.length + 1 + (HARD_COVER ? 1 : 0);
  assert(modifiers.size === expected, `${modifiers.size} steering modifiers, expected ${expected}`);
});

test('steer.shield_outweighs_vanilla_while_steered', () => {
  const entries = [...defsOf(SHIELD).values()];
  for (const type of ['knight', 'commander']) {
    const own = entries.filter((e) => field(e, 'type').value === type);
    assert(own.length === SHIELD_ENTRIES, `${own.length} ${type} entries, expected ${SHIELD_ENTRIES}`);
    for (const e of own) {
      assert(Number(field(field(e, 'chance'), 'base').value) === SHIELD_WEIGHT, `${e.key}: weight is not ${SHIELD_WEIGHT}`);
      const side = field(e, 'is_valid').block[0];
      assert(side.key === 'scope:combat_side' && side.block[0].key === 'has_variable' && side.block[0].value === 'ck3xbl_st_role',
        `${e.key} is not limited to steered sides`);
      assert(!field(e, 'effect'), `${e.key} has an effect (an empty entry must queue nothing)`);
    }
  }
});

// --- battle clock: payload 4, the steering start, the controller and the fix (PROTOCOL 14) -------------------------------

// The payload 4 reference vector (defender wins; the attacker must count 2116 dead, the defender 927).
const V4_VECTOR = { 0: 4, 1: 7, 2: 2, 3: 8199, 4: 927, 5: 2116, 6: 3, 8: 2116, 9: 927, 198: 12, 10: 3, 11: 1054, 12: 48, 13: 400, 130: 5, 131: 4, 132: 2 };

/** The run-file body of raw registers (r199 as given), as keypad_payload.mjs writes it. */
const runFileOf = (registers) => [
  'hidden_effect = {', '\tck3xbl_keypad_reset_effect = yes',
  ...registers.map((v, i) => (v ? `\tset_variable = { name = ck3xbl_r${i} value = ${v} }` : null)).filter(Boolean),
  '\tck3xbl_commit_effect = yes', '\tck3xbl_apply_effect = yes', '}',
].join('\n');

test('clock.commit_accepts_v4_and_logs_maxver', () => {
  const enc = encode(V4_VECTOR);
  assert(enc.registers[REG.version] === REG.steerPayloadVersion, 'the encoder did not keep r0 = 4');
  assert(enc.checksum === payloadChecksum(enc.registers, REG.payloadVersion), 'payload 4 does not use the version 3 checksum');
  for (const [label, sim] of [['keys', typeKeys(freshSim(7), enc.keys)], ['run file', (() => { const s = freshSim(7); s.runFile(enc.runFile); return s; })()]]) {
    assert(lastRecord(sim) === 'CK3XBL|IN|OK', `v4 ${label}: ${lastRecord(sim)}`);
    assert(registersOf(sim).every((v, i) => v === enc.registers[i]), `v4 ${label}: registers differ after the commit`);
    assert(sim.saved.get('ck3xbl_in_maxver') === REG.maxPayloadVersion, `v4 ${label}: ck3xbl_in_maxver = ${sim.saved.get('ck3xbl_in_maxver')}`);
  }
  const bad = typeKeys(freshSim(8), enc.keys);
  assert(lastRecord(bad) === 'CK3XBL|IN|BAD' && bad.saved.get('ck3xbl_in_maxver') === REG.maxPayloadVersion, 'IN|BAD without ck3xbl_in_maxver');
  const v5 = [...enc.registers];
  v5[REG.version] = REG.maxPayloadVersion + 1;
  v5[REG.checksum] = payloadChecksum(v5, REG.payloadVersion);
  const sim5 = freshSim(7);
  sim5.runFile(runFileOf(v5));
  assert(lastRecord(sim5) === 'CK3XBL|IN|BAD', `r0 = ${v5[0]} with a matching checksum was accepted`);
  let refused = false;
  try { encode({ ...V4_VECTOR, 0: REG.maxPayloadVersion + 1 }); } catch { refused = true; }
  assert(refused, `keypad_payload.mjs encodes r0 = ${REG.maxPayloadVersion + 1}`);
});

test('clock.encoder_v4_round_trips', () => {
  const rand = rng(41);
  for (let t = 0; t < 12; t++) {
    const values = { ...samplePayload(rand, { armies: 1 + (t % 8), chars: t }), [REG.version]: REG.steerPayloadVersion,
      [REG.killTarget.attacker]: Math.floor(rand() * 999999), [REG.killTarget.defender]: Math.floor(rand() * 5000), [REG.steerHint]: 6 + (t % 8) };
    const enc = encode(values);
    const sim = typeKeys(freshSim(values[REG.bid]), enc.keys);
    assert(lastRecord(sim) === 'CK3XBL|IN|OK', `v4 payload ${t}: ${lastRecord(sim)}`);
    assert(registersOf(sim).every((v, i) => v === enc.registers[i]), `v4 payload ${t}: registers differ`);
  }
});

test('clock.apply_steers_payload_4_and_applies_payload_3', () => {
  const apply = defsOf(APPLY_EFFECTS).get('ck3xbl_apply_effect').block[0].block;
  const v4 = `var:ck3xbl_r${REG.version} = ${REG.steerPayloadVersion}`;
  assert(apply[1]?.key === 'remove_variable' && apply[1].value === 'ck3xbl_applied_steer', 'the apply does not reset ck3xbl_applied_steer first');
  const gate = apply.find((n) => n.key === 'if' && n.block.some((c) => c.key === 'ck3xbl_apply_award_slot_effect'));
  const v3Only = gate.block.find((n) => n.key === 'if' && field(n, 'limit').block[0].key === 'NOT');
  assert(v3Only && JSON.stringify(field(v3Only, 'limit').block[0].block.map((c) => `${c.key} = ${c.value}`)) === JSON.stringify([v4]),
    'the army slots are not limited to payloads other than 4');
  assert(v3Only.block.filter((n) => n.key === 'ck3xbl_apply_army_slot_effect').length === REG.army.slots, 'not every army slot is in the payload 3 block');
  let armyCalls = 0;
  walk(apply, (n) => n.key === 'ck3xbl_apply_army_slot_effect' && armyCalls++);
  assert(armyCalls === REG.army.slots, `${armyCalls} army slot calls in the apply`);
  const steer = apply.find((n) => n.key === 'if' && field(n, 'limit').block.map((c) => `${c.key} = ${c.value}`).join(' ') === v4);
  assert(steer, 'no payload 4 block after the summary');
  const order = apply.map((n) => n.key);
  assert(order.indexOf('ck3xbl_apply_summary_effect') < apply.indexOf(steer) && apply.indexOf(steer) < order.indexOf('info_log', apply.indexOf(steer)),
    'the steering start is not between the summary and RES|END');
  const saves = steer.block.filter((n) => n.key === 'save_scope_value_as').map((n) => `${field(n, 'name').value}=${field(n, 'value').value}`);
  assert(JSON.stringify(saves) === JSON.stringify([`ck3xbl_st_att_target=var:ck3xbl_r${REG.killTarget.attacker}`,
    `ck3xbl_st_def_target=var:ck3xbl_r${REG.killTarget.defender}`, `ck3xbl_st_hint=var:ck3xbl_r${REG.steerHint}`]), `payload 4 values: ${saves}`);
  assert(steer.block.at(-1).key === 'ck3xbl_st_apply_effect', 'the payload 4 block does not start the battle clock');
  const other = apply[apply.indexOf(steer) + 1];
  assert(other.key === 'else' && other.block[0].key === 'ck3xbl_apply_outcome_effect', 'payloads 2 and 3 no longer end with the winner');
});

/** A variable of a simulated scope, or undefined. */
const varOf = (o, name) => o.vars.get(name)?.value;

test('clock.timeline_values', () => {
  const sim = new ScriptSim(MOD);
  /** A combat whose first snapshot set the day markers `d` days ago; ck3xbl_st_days = `days` when given. */
  const onDay = (d, days, endDay) => {
    sim.day = 0;
    const c = combat('Field');
    sim.call('ck3xbl_set_day_markers_effect', { root: c });
    sim.advance(d);
    if (days !== undefined) sim.setVar(c.vars, 'ck3xbl_st_days', days);
    if (endDay !== undefined) sim.setVar(c.vars, 'ck3xbl_st_end_day', endDay);
    return c;
  };
  const cday = (d, days) => sim.evalValue('ck3xbl_cday_value', onDay(d, days));
  assert(cday(4) === 4 && cday(4, 2) === 4 && cday(4, 7) === 7, 'ck3xbl_cday_value is not the larger of the markers and the pulse counter');
  const endDay = (c0, hint) => sim.evalValue('ck3xbl_st_end_day_value', onDay(c0), { saved: new Map([['ck3xbl_st_hint', hint]]) });
  for (const [c0, hint, want] of [[0, 0, PACE_DEFAULT], [0, 6, 6], [0, 3, PACE_MIN], [0, 13, 13], [0, 99, PACE_MAX], [0, 110, 10],
    [8, 6, 11], [9, 12, 12], [11, 0, HARD_END_DAY], [12, 6, HARD_END_DAY]]) {
    assert(endDay(c0, hint) === want, `end day on day ${c0} with r198 = ${hint}: ${endDay(c0, hint)}, expected ${want}`);
  }
  for (const [c0, want] of [[3, 12], [11, 13], [12, 13]]) {
    const got = sim.evalValue('ck3xbl_st_resume_end_day_value', onDay(c0, undefined, 12));
    assert(got === want, `resume end day on day ${c0}: ${got}, expected ${want}`);
  }
  for (let c0 = 0; c0 <= 12; c0++) {
    const want = Math.max(1, 12 - Math.max(c0 + 1, FIRST_DAMAGE_DAY));
    const got = sim.evalValue('ck3xbl_st_days_left_value', onDay(c0, undefined, 12).att);
    assert(got === want, `damage days left on day ${c0}: ${got}, expected ${want}`);
  }
});

test('clock.gain_rule', () => {
  const sim = new ScriptSim(MOD);
  const side = combat('Field').att;
  const next = (gain, measured, rem, obs, want) => {
    sim.setVar(side.vars, 'ck3xbl_st_gain', gain);
    sim.setVar(side.vars, 'ck3xbl_st_measured', measured);
    const saved = new Map([['ck3xbl_st_rem', rem], ['ck3xbl_st_obs', obs], ['ck3xbl_st_want', want]]);
    return sim.evalValue('ck3xbl_st_gain_next_value', side, { saved });
  };
  const near = (a, b) => Math.abs(a - b) < 1e-9;
  assert(next(3, 1, 0, 50, 0) === G_MIN && next(3, 1, -5, 50, 0) === G_MIN, 'a side that reached its target keeps killing');
  assert(near(next(1, 0, 900, 10, 300), 30), 'the first measured day does not set the gain to want / obs');
  assert(next(1, 0, 9000, 10, 3000) === G_MAX && next(1, 0, 900, 100, 1) === G_MIN, 'the first measured gain is not clamped to G_MIN..G_MAX');
  assert(near(next(2, 1, 900, 100, 150), 2 * (1 + DAMPING * 0.5)), 'the closed loop is not damped');
  assert(near(next(2, 1, 900, 10, 1000), 2 * STEP_MAX), 'one step is not capped');
  assert(near(next(2, 1, 900, 100, 0), 2 * Math.max(STEP_MIN, 1 - DAMPING)), 'a day that wants nothing more does not cut the gain');
  assert(next(1.7, 1, 900, 0, 300) === 1.7 && next(1, 0, 900, 0.5, 300) === 1, 'a day without kills changed the gain');
});

test('clock.owner_levers_follow_the_gains', () => {
  const sim = new ScriptSim(MOD);
  const lever = (owner, name) => (owner.modifiers.has(name) ? varOf(owner, name) : 0);
  const set = (g, gEnemy, dmgEnemy, laws = [], modifiers = []) => {
    const owner = character('owner', { laws, modifiers });
    sim.call('ck3xbl_st_set_owner_modifiers_effect', { root: owner, saved: new Map([['ck3xbl_st_g', g], ['ck3xbl_st_g_enemy', gEnemy], ['ck3xbl_st_dmg_enemy', dmgEnemy]]) });
    const pp = (key) => lever(owner, `ck3xbl_st_${key}_up`) - lever(owner, `ck3xbl_st_${key}_down`);
    return { dmg: pp('dmg'), tough: pp('tough'), knight: pp('knight') };
  };
  const same = (got, want, label) => assert(JSON.stringify(got) === JSON.stringify(want), `${label}: ${JSON.stringify(got)}, expected ${JSON.stringify(want)}`);
  const near = (a, b) => Math.abs(a - b) < 1e-6;
  same(set(1, 1, 0), { dmg: 0, tough: 0, knight: 0 }, 'gain 1');
  same(set(3, 1, 0), { dmg: 200, tough: 0, knight: KNIGHT_LEVER === 'follow' ? 200 : 0 }, 'gain 3');
  same(set(40, 1, 0), { dmg: DMG_UP_MAX, tough: 0, knight: KNIGHT_LEVER === 'follow' ? KNIGHT_UP_MAX : 0 }, 'gain above the cap');
  same(set(0.2, 1, 0), { dmg: -DMG_DOWN_MAX, tough: 0, knight: KNIGHT_LEVER === 'follow' ? -KNIGHT_DOWN_MAX : 0 }, 'gain 0.2');
  // The enemy's toughness covers what its damage lever could not: res = G / (1 + dmg / 100).
  same(set(1, 5, 400), { dmg: 0, tough: 0, knight: 0 }, 'enemy gain fully delivered by its damage');
  same(set(1, 5, 100), { dmg: 0, tough: -TOUGH_DOWN_MAX, knight: 0 }, 'enemy gain 5 with damage +100');
  same(set(1, 1.6, 100), { dmg: 0, tough: 25, knight: 0 }, 'enemy gain 1.6 with damage +100');
  same(set(1, 0.1, -50), { dmg: 0, tough: 400, knight: 0 }, 'enemy gain 0.1 with damage -50');
  // Known vanilla maluses lower the down levers: the owner's total stays at or above -MALUS_FLOOR %.
  const malus = set(0.05, 50, 100, ['budget_allocation_military_0'], ['hampered_by_false_information_modifier']);
  assert(-40 - 20 + malus.dmg >= -MALUS_FLOOR - 1e-6 && near(malus.dmg, -(MALUS_FLOOR - 60)), `damage lever with -60 % of maluses: ${malus.dmg}`);
  assert(-40 + malus.tough >= -MALUS_FLOOR && malus.tough === -TOUGH_DOWN_MAX, `toughness lever with the -40 % law: ${malus.tough}`);
  const removed = character('owner');
  for (const m of ['ck3xbl_st_dmg_up', 'ck3xbl_st_tough_down']) { removed.modifiers.add(m); sim.setVar(removed.vars, m, 50); }
  sim.call('ck3xbl_st_owner_levers_off_effect', { root: removed });
  assert(removed.modifiers.size === 0 && removed.vars.size === 0, 'the levers-off effect left a modifier or a variable');
});

// --- the battle clock in a toy combat ----------------------------------------------------------------------------

const CLOCK_BID = 7;
// Damage per fighting man and day at gain 1 (damage 12, toughness 10.5, 3 % per day), and the share of the
// casualties that die without the hard modifier (BASE_RATIO_CASUALTIES_CONVERSION 0.3).
const TOY_RATE = (0.03 * 12) / 10.5;
const NATIVE_DEAD_SHARE = 0.3;
// The hard tags of the generated steering modifiers (ck3xbl_st_hard, ck3xbl_st_hard_cover): modifier -> { tag: value }.
const HARD_TAGS = (() => {
  const text = stripBom(fs.readFileSync(path.join(MOD, 'common', 'modifiers', 'ck3xbl_gen_steer_modifiers.txt'), 'utf8'));
  const out = new Map();
  for (const n of parseText(text)) {
    const tags = Object.fromEntries(n.block.filter((c) => /hard_casualty_modifier$/.test(c.key)).map((c) => [c.key, Number(c.value)]));
    if (Object.keys(tags).length) out.set(n.key, tags);
  }
  return out;
})();
/** The sum of one hard tag over a character's modifiers (modifiers on one character add up). */
const hardTag = (ch, tag) => [...ch.modifiers].reduce((n, m) => n + (HARD_TAGS.get(m)?.[tag] || 0), 0);
/** The commanders of a side: its side list and the commanders of its armies in the combat. */
const sideCommanders = (c, side) => [...new Set([...(side.commanders || []),
  ...c.location.armies.filter((a) => a.side === side && a.commander).map((a) => a.commander)])];
/**
 * The dead share of the casualties of `side` (measured in game; live test 2026-10-04 clash 2): 0.3 x (1 + the side's own
 * hard_casualty_modifier + the enemy's enemy_hard_casualty_modifier), one value per side (here the largest of its commanders),
 * not capped at 1. A side without a commander contributes neither of its terms.
 */
function deadShare(c, side) {
  const best = (s, tag) => Math.max(0, ...sideCommanders(c, s).map((x) => hardTag(x, tag)));
  return NATIVE_DEAD_SHARE * (1 + best(side, 'hard_casualty_modifier') + best(side.enemy, 'enemy_hard_casualty_modifier'));
}

/** A combat of tagged armies (men per army and side), the player, and the day markers of its first snapshot. */
function clockWorld({ sides, laws = [[], []], noCommander = [false, false], stubs = {} }) {
  const sim = new ScriptSim(MOD, { stubs });
  const player = character('player');
  sim.player = player;
  const c = combat('Field');
  for (const scope of [c, c.att, c.def]) sim.setVar(scope.vars, 'ck3xbl_bid', CLOCK_BID);
  let aid = 0;
  const armies = [];
  [c.att, c.def].forEach((side, s) => sides[s].forEach((men, k) => {
    const owner = character(`side ${s + 1} owner ${k}`, { laws: laws[s] });
    const commander = noCommander[s] ? null : character(`side ${s + 1} commander ${k}`);
    const a = army(`side ${s + 1} army ${k}`, { owner, commander, levies: men });
    joinCombat(a, side);
    sim.setVar(a.vars, 'ck3xbl_aid', ++aid);
    sim.setVar(a.vars, 'ck3xbl_bid', CLOCK_BID);
    sim.setVar(a.vars, 'ck3xbl_aside', s + 1);
    sim.list('ck3xbl_armies').push(a);
    armies.push(a);
  }));
  sim.call('ck3xbl_set_day_markers_effect', { root: c });
  return { sim, player, c, armies };
}

/** Runs ck3xbl_st_apply_effect as the payload 4 apply does: registers, summary values and the saved values of the apply. */
function applyV4(w, { outcome, targets, hint = 0, bid = CLOCK_BID, slots = [], live = true, summary = {} }) {
  const { sim, player, c } = w;
  for (let i = 0; i < REG.count; i++) sim.setVar(player.vars, `ck3xbl_r${i}`, 0);
  slots.forEach((slot, i) => {
    const base = REG.army.base + REG.army.stride * i;
    sim.setVar(player.vars, `ck3xbl_r${base}`, slot.tag);
    sim.setVar(player.vars, `ck3xbl_r${base + 1}`, slot.levy);
    (slot.pairs || []).forEach(([type, share], k) => {
      sim.setVar(player.vars, `ck3xbl_r${base + 2 + 2 * k}`, type);
      sim.setVar(player.vars, `ck3xbl_r${base + 3 + 2 * k}`, share);
    });
  });
  const saved = new Map([['ck3xbl_bid', bid], ['ck3xbl_res_outcome', outcome], ['ck3xbl_res_has_primaries', 0], ['ck3xbl_res_has_prov', 0],
    ['ck3xbl_res_my_side', 1], ['ck3xbl_st_att_target', targets[0]], ['ck3xbl_st_def_target', targets[1]], ['ck3xbl_st_hint', hint]]);
  // The summary values ck3xbl_apply_summary_effect saved earlier in the apply.
  for (const k of ['my_lost', 'enemy_lost', 'my_kills', 'won']) saved.set(`ck3xbl_res_${k}`, summary[k] ?? 0);
  if (live) saved.set('ck3xbl_res_combat', c);
  return sim.call('ck3xbl_st_apply_effect', { root: player, saved });
}

const sideMen = (c, side) => c.location.armies.filter((a) => a.side === side).reduce((n, a) => n + a.levies + a.regiments.reduce((m, r) => m + r.cur, 0), 0);
/** The fighting men of a side (side_soldiers): its men less those routed (soft casualties, out of the fight for good). */
const fighting = (c, side) => Math.max(0, sideMen(c, side) - (side.routed || 0));

/** Owner lever (percent points) of a side, weighted by men: what CK3 applies while the modifier is on the owner. */
function sideLever(c, side, key) {
  let men = 0;
  let sum = 0;
  for (const a of c.location.armies.filter((x) => x.side === side)) {
    const pp = (dir) => (a.owner.modifiers.has(`ck3xbl_st_${key}_${dir}`) ? varOf(a.owner, `ck3xbl_st_${key}_${dir}`) || 0 : 0);
    men += a.levies;
    sum += a.levies * (pp('up') - pp('down'));
  }
  return men ? sum / men : 0;
}

/** The dead of `side`: taken from its armies in proportion to their men. */
function inflict(c, side, dead) {
  const armies = c.location.armies.filter((a) => a.side === side);
  const total = armies.reduce((n, a) => n + a.levies, 0);
  if (!total) return;
  let left = Math.min(dead, total);
  for (const a of armies) {
    const cut = Math.min(a.levies, Math.round((dead * a.levies) / total));
    a.levies -= cut;
    left -= cut;
  }
  for (const a of armies) {
    const cut = Math.min(a.levies, Math.max(0, left));
    a.levies -= cut;
    left -= cut;
  }
}

/**
 * Fights the steered combat out day by day after the apply on `start`: the day's damage (toy model,
 * damage from day FIRST_DAMAGE_DAY, width = the mean of both sides' starting men, an advantage roll 1.0-2.35 on one side that
 * changes on days 6, 9 and 12), then the mod's pulse (as in game: after the damage). The day's casualties of a side leave its fighting
 * men; deadShare() of them die (the enemy's kills), the rest rout; a share above 1 kills routed men as well.
 * `events[day](w)` runs before that day's damage, `after[day](w)` after that day's pulse.
 * Returns the daily trace and the end (the day of the end call, the day CK3 ends the combat, or the hand-back).
 */
function fightOut(w, { start, seed, rollSide, events = {}, after = {} }) {
  const { sim, player, c } = w;
  const rand = rng(seed);
  const width = (sideMen(c, c.att) + sideMen(c, c.def)) / 2;
  const rolls = new Map();
  const roll = (day) => {
    const period = day < 6 ? 0 : day < 9 ? 1 : day < 12 ? 2 : 3;
    if (!rolls.has(period)) rolls.set(period, 1 + 1.35 * rand());
    return rolls.get(period);
  };
  const trace = [];
  for (let day = start + 1; day <= HARD_END_DAY + 2; day++) {
    if (Object.keys(c.att.flags).includes('set_winner') || Object.keys(c.def.flags).includes('set_winner')) return { trace, endedOn: day };
    sim.advance(day);
    events[day]?.(w);
    c.att.soldiers = fighting(c, c.att);
    c.def.soldiers = fighting(c, c.def);
    const kills = [0, 0];
    if (day >= FIRST_DAMAGE_DAY) {
      const casualties = [0, 0];
      [[c.att, c.def], [c.def, c.att]].forEach(([x, y], i) => {
        const r = (rollSide === i + 1 ? roll(day) : 1);
        const dmg = 1 + sideLever(c, x, 'dmg') / 100;
        const tough = 1 + sideLever(c, y, 'tough') / 100;
        casualties[i] = Math.min(y.soldiers, Math.round((TOY_RATE * Math.min(x.soldiers, width) * r * dmg) / tough));
        kills[i] = Math.min(Math.round(casualties[i] * deadShare(c, y)), casualties[i] + (y.routed || 0));
      });
      [[c.att, c.def], [c.def, c.att]].forEach(([x, y], i) => {
        x.killed += kills[i];
        y.routed = (y.routed || 0) + casualties[i] - kills[i];
        inflict(c, y, kills[i]);
      });
    }
    sim.call('ck3xbl_bump_global_counter_effect', { root: player, params: { NAME: 'ck3xbl_clock_tick' } });
    sim.call('ck3xbl_st_pulse_tick_effect', { root: player, scope: c });
    const owners = [...new Set(c.location.armies.map((a) => a.owner))];
    trace.push({ day, kills, killed: [c.att.killed, c.def.killed], gain: [varOf(c.att, 'ck3xbl_st_gain'), varOf(c.def, 'ck3xbl_st_gain')],
      men: [fighting(c, c.att), fighting(c, c.def)], share: [deadShare(c, c.att), deadShare(c, c.def)],
      owners: owners.map((o) => ({ owner: o, dmg: sideLeverOf(o, 'dmg'), tough: sideLeverOf(o, 'tough') })) });
    if (varOf(c, 'ck3xbl_steer') === 4) return { trace, handedBackOn: day };
    after[day]?.(w);
  }
  return { trace };
}

const sideLeverOf = (owner, key) => (owner.modifiers.has(`ck3xbl_st_${key}_up`) ? varOf(owner, `ck3xbl_st_${key}_up`) : 0) -
  (owner.modifiers.has(`ck3xbl_st_${key}_down`) ? varOf(owner, `ck3xbl_st_${key}_down`) : 0);

/** Every ST record the simulation logged, with its saved values (stage -> [Map]). */
const stRecords = (sim, stage) => sim.log.filter((l) => l.record === `CK3XBL|ST|${stage}`).map((l) => l.saved);

/** on_combat_end_winner / _loser of `side` as the on_action runs it: scope:ck3xbl_bid = the combat's bid, then the battle clock. */
const combatEnd = (sim, side, wrongRole) => sim.call('ck3xbl_st_on_combat_end_effect',
  { root: side, params: { WRONG_ROLE: wrongRole }, saved: new Map([['ck3xbl_bid', varOf(side.combat, 'ck3xbl_bid')]]) });

// Controller cases. winner 1 = attacker, 2 = defender; targets = [attacker's kills, defender's kills].
const CONTROLLER_CASES = [
  { name: 'Hastings', sides: [[3230], [3177]], winner: 1, targets: [2116, 927] },
  { name: '500 v 400', sides: [[500], [400]], winner: 1, targets: [300, 100] },
  { name: '8000 v 6000', sides: [[5000, 3000], [6000]], winner: 1, targets: [4000, 1500] },
  { name: 'small winner, big loser', sides: [[500], [3000]], winner: 1, targets: [2000, 300] },
  { name: 'defender wins', sides: [[3000], [2500]], winner: 2, targets: [600, 1800] },
  { name: 'pace 13', sides: [[3230], [3177]], winner: 1, targets: [2116, 927], hint: 13 },
  { name: 'click on day 6', sides: [[3230], [3177]], winner: 1, targets: [2116, 927], start: 6 },
  { name: 'winner joined on day 6 (+50 %)', sides: [[3230], [3177]], winner: 1, targets: [2116, 927], join: { day: 6, side: 1, men: 1615 } },
  { name: 'big winner, few dead, law malus', sides: [[6000], [1000]], winner: 1, targets: [300, 200], laws: true },
];

test('clock.controller_reaches_the_targets', () => {
  const lines = [];
  for (const k of CONTROLLER_CASES) {
    for (let seed = 1; seed <= 10; seed++) {
      const w = clockWorld({ sides: k.sides, laws: k.laws ? [['budget_allocation_military_0'], ['budget_allocation_military_0']] : [[], []] });
      const start = k.start ?? 1;
      w.sim.advance(start);
      applyV4(w, { outcome: k.winner, targets: k.targets, hint: k.hint ?? 0 });
      const begin = stRecords(w.sim, 'BEGIN').at(-1);
      const E = begin.get('ck3xbl_st_end_day');
      const events = {};
      if (k.join) {
        events[k.join.day] = ({ sim, c }) => {
          const side = k.join.side === 1 ? c.att : c.def;
          const joiner = army('joiner', { owner: character('joiner owner'), commander: character('joiner commander'), levies: k.join.men });
          joinCombat(joiner, side);
          const ctx = sim.call('ck3xbl_st_classify_join_effect', { root: side, saved: new Map([['army', joiner]]) });
          assert(ctx.saved.get('ck3xbl_j_steer') === 1, `${k.name}: the winner-side join is classified ${ctx.saved.get('ck3xbl_j_steer')}`);
          sim.call('ck3xbl_st_after_join_effect', { root: side, saved: ctx.saved });
          assert(varOf(joiner, 'ck3xbl_st_bid') === CLOCK_BID && varOf(joiner, 'ck3xbl_st_joiner') === 1, `${k.name}: the joiner is not steered`);
          assert(joiner.commander.modifiers.has('ck3xbl_st_hard'), `${k.name}: the joiner's commander has no hard modifier`);
        };
      }
      const run = fightOut(w, { start, seed, rollSide: 1 + (seed % 2), events });
      const win = stRecords(w.sim, 'WIN');
      assert(win.length === 1, `${k.name} seed ${seed}: ${win.length} ST|WIN records`);
      const wi = k.winner - 1;
      const last = run.trace.at(-1);
      const K = last.killed[wi];
      const T = k.targets[wi];
      const endDay = last.day;
      for (const t of run.trace) {
        for (const g of t.gain) assert(g >= G_MIN - 1e-9 && g <= G_MAX + 1e-9, `${k.name} seed ${seed}: gain ${g} on day ${t.day}`);
        for (const o of t.owners) {
          const malus = o.owner.laws.has('budget_allocation_military_0') ? -40 : 0;
          assert(malus + o.dmg >= -MALUS_FLOOR && malus + o.tough >= -MALUS_FLOOR, `${k.name} seed ${seed}: owner total below -${MALUS_FLOOR} % on day ${t.day}`);
        }
      }
      assert(endDay <= HARD_END_DAY, `${k.name} seed ${seed}: the end call came on day ${endDay}`);
      assert(endDay <= E, `${k.name} seed ${seed}: the end call came on day ${endDay}, after the end day ${E}`);
      assert(K >= 0.9 * T, `${k.name} seed ${seed}: the winner counted ${K} of ${T}`);
      assert(K - T <= Math.max(1, last.kills[wi]), `${k.name} seed ${seed}: overshoot ${K - T} > the last day's ${last.kills[wi]} kills`);
      assert(win[0].get('ck3xbl_st_capped') === 0, `${k.name} seed ${seed}: capped`);
      assert(run.endedOn === endDay + 1, `${k.name} seed ${seed}: the combat did not end the day after the end call`);
      lines.push(`${k.name} #${seed}: end call day ${endDay} (E ${E}), K/T ${(K / T).toFixed(3)}, gains ${run.trace.map((t) => t.gain[wi].toFixed(2)).join(' ')}`);
    }
  }
  // Diagnostic: CK3XBL_CLOCK_TRACE=1 node tools/gen/test_ck3_mod.mjs clock.controller
  if (process.env.CK3XBL_CLOCK_TRACE) for (const l of lines) console.log(l);
});

/**
 * The live test of 2026-10-04 (battles b2_ea00 -> b3_ea00, finding 3 of that test): the Bannerlord
 * winner (the attacker, 1,336 men under a commander) against the loser's 250 men without a commander; clash 1 steers to 247/28
 * from day 1; after the pulse of day 6, 900 fresh men without a commander join the loser, steering is suspended and clash 2 (the
 * child) is applied the same day with the cumulative targets 1,024/184, i.e. the loser loses about 90 % of all its men.
 * `stubs` replaces mod effects (the no-cover baseline), `pace` is r198.
 */
function liveClashTwo({ seed, stubs = {}, pace = 12 }) {
  const w = clockWorld({ sides: [[1336], [250]], noCommander: [false, true], stubs });
  const { sim, c, player } = w;
  const child = CLOCK_BID + 1;
  sim.advance(1);
  applyV4(w, { outcome: 1, targets: [247, 28], hint: pace });
  const after = {
    6: () => {
      const r = army('reinforcement', { owner: w.armies[1].owner, levies: 900 });
      joinCombat(r, c.def);
      sim.setVar(r.vars, 'ck3xbl_aid', 99);
      sim.setVar(r.vars, 'ck3xbl_aside', 2);
      sim.list('ck3xbl_armies').push(r);
      // ck3xbl_st_suspend_effect and the child's snapshot (as suspendedWorld), then the child's payload on the same day: the game
      // stays paused while clash 2 is fought in Bannerlord.
      for (const x of [c, ...c.location.armies]) sim.setVar(x.vars, 'ck3xbl_bid', child);
      sim.setVar(c.vars, 'ck3xbl_st_suspended', 1);
      sim.setVar(c.vars, 'ck3xbl_st_susp_days', 0);
      sim.setVar(c.vars, 'ck3xbl_st_child', child);
      for (const e of ['ck3xbl_st_remove_owner_levers_effect', 'ck3xbl_st_remove_hard_effect']) sim.call(e, { root: player, saved: new Map([['ck3xbl_st_cur_bid', CLOCK_BID]]) });
      applyV4(w, { outcome: 1, targets: [1024, 184], bid: child, hint: pace });
    },
  };
  const run = fightOut(w, { start: 1, seed, rollSide: 1 + (seed % 2), after });
  return { w, run, K: run.trace.at(-1).killed[0], T: 1024 };
}

test('clock.reinforced_loser_without_a_commander_reaches_its_dead', () => {
  const lines = [];
  for (let seed = 1; seed <= 10; seed++) {
    // As in the live test before the cover: no commander on the loser's side, so its dead share is 0.3 x (1 + 1.16) = 0.65 and its
    // fighters run out before its dead reach the target (live: 778 of 1,024 by day 12).
    const old = liveClashTwo({ seed, stubs: { ck3xbl_st_keep_hard_cover_effect: () => {} } });
    assert(old.run.trace.filter((d) => d.day >= 7).every((d) => Math.abs(d.share[1] - 0.648) < 0.01), `seed ${seed}: the loser's dead share without the cover is not 0.65`);
    assert(old.K < 0.9 * old.T, `seed ${seed}: without the cover the winner still counted ${old.K} of ${old.T}`);
    // With the cover (HARD_COVER): the winner's commander carries both terms once more for the leaderless loser.
    const now = liveClashTwo({ seed });
    const { w, run, K, T } = now;
    const days7 = run.trace.filter((d) => d.day >= 7);
    assert(days7.every((d) => d.share[1] > 0.99), `seed ${seed}: the loser's dead share with the cover is ${days7.map((d) => d.share[1].toFixed(2))}`);
    const att = w.armies[0].commander;
    const win = stRecords(w.sim, 'WIN');
    assert(win.length === 1, `seed ${seed}: ${win.length} ST|WIN records`);
    assert(K >= 0.9 * T, `seed ${seed}: the winner counted ${K} of ${T}`);
    assert(K - T <= Math.max(1, run.trace.at(-1).kills[0]), `seed ${seed}: overshoot ${K - T} > the last day's ${run.trace.at(-1).kills[0]} kills`);
    assert(run.trace.at(-1).day <= HARD_END_DAY, `seed ${seed}: the end call came on day ${run.trace.at(-1).day}`);
    for (const d of run.trace) for (const g of d.gain) assert(g >= G_MIN - 1e-9 && g <= G_MAX + 1e-9, `seed ${seed}: gain ${g} on day ${d.day}`);
    // The fix takes the hard modifier and its cover off again.
    for (const a of w.c.location.armies) a.side = null;
    w.sim.call('ck3xbl_st_fix_effect', { root: w.player, saved: new Map([['ck3xbl_st_cur_bid', CLOCK_BID + 1]]) });
    assert(!att.modifiers.size && !att.vars.has('ck3xbl_st_cover_bid'), `seed ${seed}: the fix left ${[...att.modifiers]} on the winner's commander`);
    lines.push(`seed ${seed}: without cover K/T ${(old.K / old.T).toFixed(3)} (end call day ${old.run.trace.at(-1).day}), with cover ` +
      `${(K / T).toFixed(3)} (end call day ${run.trace.at(-1).day}); loser men ${days7.map((d) => d.men[1]).join(' ')}; ` +
      `gains ${days7.map((d) => d.gain[0].toFixed(2)).join(' ')}`);
  }
  // Diagnostic: CK3XBL_CLOCK_TRACE=1 node tools/gen/test_ck3_mod.mjs clock.reinforced
  if (process.env.CK3XBL_CLOCK_TRACE) for (const l of lines) console.log(l);
});

test('clock.controller_edge_cases', () => {
  // T = 0: the winner has nothing to kill, so the end call comes on the first pulse and no lever is ever set.
  {
    const w = clockWorld({ sides: [[1000], [1000]] });
    w.sim.advance(1);
    applyV4(w, { outcome: 1, targets: [0, 300] });
    const run = fightOut(w, { start: 1, seed: 1, rollSide: 1 });
    assert(run.trace.length === 1 && stRecords(w.sim, 'WIN').length === 1, `T = 0: the end call came on day ${run.trace.at(-1).day}`);
    assert(run.trace.every((t) => t.owners.every((o) => o.dmg === 0 && o.tough === 0)), 'T = 0: a lever was set');
  }
  // Unreachable: the end day is extended while the winner sits at G_MAX far from its target, up to day 13; ST|WIN says capped.
  {
    const w = clockWorld({ sides: [[300], [6000]] });
    w.sim.advance(1);
    applyV4(w, { outcome: 1, targets: [5000, 100] });
    const run = fightOut(w, { start: 1, seed: 3, rollSide: 2 });
    const win = stRecords(w.sim, 'WIN')[0];
    assert(run.trace.at(-1).day === HARD_END_DAY && win.get('ck3xbl_st_cday') === HARD_END_DAY, `unreachable: the end call came on day ${run.trace.at(-1).day}`);
    assert(win.get('ck3xbl_st_capped') === 1 && win.get('ck3xbl_st_att_short') > 0, 'unreachable: ST|WIN is not capped with a shortfall');
  }
  // The end day with the winner far from its target below the gain cap (live test 2026-10-04): one more day while the loser still
  // has fighting men, the end call once it has none (more days cannot add dead then).
  for (const [men, extended] of [[300, true], [0, false]]) {
    const w = clockWorld({ sides: [[1000], [1000]] });
    const { sim, c, player } = w;
    sim.advance(1);
    applyV4(w, { outcome: 1, targets: [800, 100] });
    sim.advance(12);
    c.att.killed = 500;
    sim.setVar(c.att.vars, 'ck3xbl_st_killed', 500);
    sim.setVar(c.att.vars, 'ck3xbl_st_gain', 5);
    sim.setVar(c.att.vars, 'ck3xbl_st_obs_last', 60);
    c.def.soldiers = men;
    sim.call('ck3xbl_st_end_check_effect', { root: player, scope: c, saved: new Map([['ck3xbl_st_cday', 12], ['ck3xbl_st_cur_bid', CLOCK_BID]]) });
    const ended = Object.keys(c.att.flags).includes(END_EFFECT);
    assert(ended === !extended && varOf(c, 'ck3xbl_st_end_day') === (extended ? 13 : 12),
      `end day with ${men} loser fighters: ended ${ended}, end day ${varOf(c, 'ck3xbl_st_end_day')}`);
  }
  // Suspension on days 6-7 (a second clash in Bannerlord): no levers and no hard modifier, the baselines stay fresh; the
  // child's ST|BEGIN (cumulative targets, here the same) re-seeds both sides and the battle still reaches its targets.
  {
    const w = clockWorld({ sides: [[3230], [3177]] });
    const { sim, c, player } = w;
    sim.advance(1);
    applyV4(w, { outcome: 1, targets: [2116, 927] });
    const events = {
      6: () => {
        sim.setVar(c.vars, 'ck3xbl_st_suspended', 1);
        sim.setVar(c.vars, 'ck3xbl_st_susp_days', 0);
        sim.setVar(c.vars, 'ck3xbl_st_child', CLOCK_BID + 1);
        sim.setVar(sim.globals, 'ck3xbl_pending_bid', CLOCK_BID + 1);
        sim.call('ck3xbl_st_remove_owner_levers_effect', { root: player, saved: new Map([['ck3xbl_st_cur_bid', CLOCK_BID]]) });
        sim.call('ck3xbl_st_remove_hard_effect', { root: player, saved: new Map([['ck3xbl_st_cur_bid', CLOCK_BID]]) });
      },
      8: () => {
        for (const a of w.armies) sim.setVar(a.vars, 'ck3xbl_bid', CLOCK_BID + 1);
        sim.globals.delete('ck3xbl_pending_bid');
        applyV4(w, { outcome: 1, targets: [2116, 927], bid: CLOCK_BID + 1 });
      },
    };
    const run = fightOut(w, { start: 1, seed: 5, rollSide: 1, events });
    const p = stRecords(sim, 'P');
    assert(p.filter((v) => v.get('ck3xbl_st_susp') === 1).length === 2, 'suspension: not two suspended pulses');
    const begins = stRecords(sim, 'BEGIN');
    assert(begins.length === 2 && begins[1].get('ck3xbl_st_parent') === CLOCK_BID, 'suspension: the child ST|BEGIN does not name its parent');
    assert(run.trace.filter((t) => t.day >= 6 && t.day <= 7).every((t) => t.owners.every((o) => o.dmg === 0 && o.tough === 0)), 'suspension: levers while suspended');
    const K = run.trace.at(-1).killed[0];
    assert(stRecords(sim, 'WIN').length === 1 && K >= 0.85 * 2116, `suspension: the winner counted ${K} of 2116`);
    assert(varOf(c, 'ck3xbl_st_bid') === CLOCK_BID + 1 && w.armies.every((a) => varOf(a, 'ck3xbl_st_bid') === CLOCK_BID + 1), 'suspension: one steering bid after the child');
  }
  // Hand-back mode (outcome 3): steer to both targets, then hand the combat back without a winner.
  {
    const w = clockWorld({ sides: [[2000], [2000]] });
    w.sim.advance(1);
    applyV4(w, { outcome: 3, targets: [600, 500] });
    const run = fightOut(w, { start: 1, seed: 2, rollSide: 1 });
    const hb = stRecords(w.sim, 'HANDBACK');
    assert(run.handedBackOn && hb.length === 1 && hb[0].get('ck3xbl_st_reason') === 2, 'hand-back: no ST|HANDBACK reason 2');
    assert(stRecords(w.sim, 'WIN').length === 0 && !Object.keys(w.c.att.flags).includes('set_winner'), 'hand-back: a winner was called');
    const k = run.trace.at(-1).killed;
    assert(k[0] >= 0.9 * 600 && k[1] >= 0.9 * 500, `hand-back: kills ${k} of 600/500`);
    assert(!w.c.att.vars.has('ck3xbl_st_role') && w.armies.every((a) => !a.owner.modifiers.size && !a.commander.modifiers.size), 'hand-back: modifiers or roles left');
    assert(w.armies.every((a) => varOf(a, 'ck3xbl_st_fix_armed') === 2), 'hand-back: the fix is not armed with reason 2');
  }
});

test('clock.end_call_once_and_backstop', () => {
  // The END pair arms the fix once (reason 1); a combat still going END_BACKSTOP_DAYS pulses after the end call is handed back.
  const w = clockWorld({ sides: [[1000], [1000]] });
  const { sim, c, player } = w;
  sim.advance(1);
  applyV4(w, { outcome: 2, targets: [100, 0] });
  sim.call('ck3xbl_st_end_effect', { root: player, scope: c, saved: new Map([['ck3xbl_st_cur_bid', CLOCK_BID]]) });
  assert(Object.keys(c.def.flags).includes(END_EFFECT) && Object.keys(c.att.flags).includes('set_skip_pursuit'), 'the end call is not the configured effect on the winner');
  assert(!Object.keys(c.def.flags).includes('force_win'), 'force_win was called');
  // An army that joined after the end call (j_steer 0): tagged by its J record, never steered.
  const late = army('late joiner', { owner: character('late owner'), levies: 300 });
  joinCombat(late, c.att);
  for (const [k, v] of [['ck3xbl_aid', 9], ['ck3xbl_bid', CLOCK_BID], ['ck3xbl_aside', 1]]) sim.setVar(late.vars, k, v);
  sim.list('ck3xbl_armies').push(late);
  const events = sim.events.length;
  combatEnd(sim, c.def, 2);
  combatEnd(sim, c.att, 1);
  const fix = sim.events.slice(events).filter((e) => e.id === 'ck3xbl_clock.0002');
  assert(fix.length === 1 && fix[0].days === 1 && fix[0].saved.get('ck3xbl_st_cur_bid') === CLOCK_BID, `the END pair armed ${fix.length} fixes`);
  assert(w.armies.every((a) => varOf(a, 'ck3xbl_st_fix_armed') === 1), 'the fix is not armed with reason 1');
  assert(!sim.list('ck3xbl_armies').includes(late) && late.vars.size === 0, 'the END did not release the unsteered joiner');
  assert(w.armies.every((a) => sim.list('ck3xbl_armies').includes(a) && varOf(a, 'ck3xbl_bid') === CLOCK_BID), 'the END released a steered army');

  const w2 = clockWorld({ sides: [[1000], [1000]] });
  w2.sim.advance(1);
  applyV4(w2, { outcome: 1, targets: [100, 0] });
  w2.sim.call('ck3xbl_st_end_effect', { root: w2.player, scope: w2.c, saved: new Map([['ck3xbl_st_cur_bid', CLOCK_BID]]) });
  for (let d = 2; d <= 1 + END_BACKSTOP_DAYS; d++) {
    w2.sim.advance(d);
    w2.sim.call('ck3xbl_bump_global_counter_effect', { root: w2.player, params: { NAME: 'ck3xbl_clock_tick' } });
    w2.sim.call('ck3xbl_st_pulse_tick_effect', { root: w2.player, scope: w2.c });
  }
  const hb = stRecords(w2.sim, 'HANDBACK');
  assert(hb.length === 1 && hb[0].get('ck3xbl_st_reason') === 6, 'the backstop did not hand the battle back with reason 6');
  assert(w2.armies.every((a) => varOf(a, 'ck3xbl_st_fix_armed') === 6), 'the backstop did not arm the fix with reason 6');

  // The Bannerlord loser won anyway: reason 5.
  const w3 = clockWorld({ sides: [[1000], [1000]] });
  w3.sim.advance(1);
  applyV4(w3, { outcome: 1, targets: [100, 0] });
  combatEnd(w3.sim, w3.c.def, 2);
  assert(w3.armies.every((a) => varOf(a, 'ck3xbl_st_fix_armed') === 5), 'a win of the Bannerlord loser is not reason 5');
});

test('clock.second_clash_offer_classification', () => {
  const classify = ({ role, steer = 1, suspended = false, men, pending = 0 }) => {
    const w = clockWorld({ sides: [[1000], [1000]] });
    const { sim, c } = w;
    sim.setVar(c.vars, 'ck3xbl_bid', CLOCK_BID);
    if (role) sim.setVar(c.att.vars, 'ck3xbl_st_role', role);
    if (steer) sim.setVar(c.vars, 'ck3xbl_steer', steer);
    if (suspended) sim.setVar(c.vars, 'ck3xbl_st_suspended', 1);
    if (pending) sim.setVar(sim.globals, 'ck3xbl_pending_bid', pending);
    sim.setVar(sim.globals, 'ck3xbl_pending_fresh', 1);
    sim.setVar(sim.globals, 'ck3xbl_pending_player', w.player);
    const joiner = army('joiner', { owner: character('joiner owner'), levies: men });
    joinCombat(joiner, c.att);
    c.att.soldiers = sideMen(c, c.att);
    return sim.call('ck3xbl_st_classify_join_effect', { root: c.att, saved: new Map([['army', joiner]]) }).saved.get('ck3xbl_j_steer');
  };
  const cases = [
    [{ role: 0, men: 500 }, 0, 'not steered'],
    [{ role: 2, steer: 3, men: 500 }, 0, 'ending'],
    [{ role: 1, men: 500 }, 1, "the Bannerlord winner's side"],
    [{ role: 2, men: 500 }, 2, 'loser side, enough men'],
    [{ role: 2, men: 99 }, 3, 'loser side, under 100 men'],
    [{ role: 2, men: 105 }, 3, "loser side, under 10 % of the side's men"],
    [{ role: 2, men: 500, suspended: true }, 3, 'loser side while suspended'],
    [{ role: 2, men: 500, pending: 99 }, 3, 'loser side, the field busy'],
    [{ role: 2, steer: 2, men: 500 }, 3, 'loser side in hand-back mode'],
    [{ role: 3, steer: 2, men: 500 }, 4, 'hand-back mode, no winner'],
  ];
  for (const [spec, want, label] of cases) {
    const got = classify(spec);
    assert(got === want, `${label}: ck3xbl_j_steer = ${got}, expected ${want}`);
  }
});

// --- slot storage and the fix ------------------------------------------------------------------------------------

test('clock.slot_storage_default_state_and_fix', () => {
  const maa = readData('ck3_maa_index.json').keys;
  const idx = (key) => maa.indexOf(key) + 1;
  const w = clockWorld({ sides: [[0, 500], [800]] });
  const { sim, c, player } = w;
  const [A, B, D] = w.armies;
  A.levies = 1000;
  A.regiments = [{ kind: 'regiment', type: 'armored_footmen', cur: 200, max: 200 }, { kind: 'regiment', type: 'bowmen', cur: 150, max: 200 }];
  B.regiments = [{ kind: 'regiment', type: 'light_horsemen', cur: 100, max: 100 }];
  D.regiments = [{ kind: 'regiment', type: 'ck3xbl_test_unindexed', cur: 30, max: 30 }];
  sim.advance(1);
  // Clash 1: A keeps 700 levies and half its armored footmen; D keeps 600 levies; B is in no slot.
  applyV4(w, { outcome: 1, targets: [300, 400], slots: [{ tag: 1, levy: 700, pairs: [[idx('armored_footmen'), 500]] }, { tag: 3, levy: 600 }] });
  assert(varOf(A, 'ck3xbl_t_levy') === 700 && varOf(A, 'ck3xbl_t_m1_idx') === idx('armored_footmen') && varOf(A, 'ck3xbl_t_m1_share') === 500,
    'slot 0 is not stored on army A');
  assert(!B.vars.has('ck3xbl_t_levy') && varOf(B, 'ck3xbl_td_levy') === 500, 'army B (no slot) has no steering-start state');
  assert(varOf(A, 'ck3xbl_td_levy') === 1000 && varOf(A, `ck3xbl_td_m${idx('bowmen')}_cur`) === 150 && varOf(A, `ck3xbl_td_m${idx('bowmen')}_max`) === 200,
    'army A: steering-start state');
  // CK3 fights on; a second clash names only D (its end state is overwritten) and keeps A's clash-1 state and every default.
  A.levies = 900;
  for (const x of [c, ...w.armies]) sim.setVar(x.vars, 'ck3xbl_bid', CLOCK_BID + 1);
  applyV4(w, { outcome: 1, targets: [300, 400], bid: CLOCK_BID + 1, slots: [{ tag: 3, levy: 550 }] });
  assert(varOf(A, 'ck3xbl_td_levy') === 1000 && varOf(A, 'ck3xbl_t_levy') === 700, 'the second clash changed army A\'s stored states');
  assert(varOf(D, 'ck3xbl_t_levy') === 550, 'the second clash did not overwrite army D\'s end state');
  // CK3's own losses, then the combat ends.
  A.levies = 650; A.regiments[0].cur = 120; A.regiments[1].cur = 100;
  B.levies = 300; B.regiments[0].cur = 60;
  D.levies = 700; D.regiments[0].cur = 10;
  for (const a of w.armies) a.side = null;
  combatEnd(sim, c.att, 2);
  sim.advance(sim.day + 1);
  const fix = sim.events.find((e) => e.id === 'ck3xbl_clock.0002');
  sim.call('ck3xbl_st_fix_effect', { root: player, saved: fix.saved });
  assert(A.levies === 650 && A.regiments[0].cur === 100 && A.regiments[1].cur === 150, `army A after the fix: ${A.levies} levies, ${A.regiments.map((r) => r.cur)}`);
  assert(B.levies === 300 && B.regiments[0].cur === 100, `army B after the fix: ${B.levies} levies, ${B.regiments[0].cur} light horsemen`);
  assert(D.levies === 550 && D.regiments[0].cur === Math.round(10 * (550 / 700)), `army D after the fix: ${D.levies} levies, ${D.regiments[0].cur} unindexed`);
  const fixes = stRecords(sim, 'FIX');
  const of = (tag) => fixes.find((v) => v.get('ck3xbl_a_tag') === tag);
  assert(fixes.length === 3, `${fixes.length} ST|FIX records`);
  assert(of(1).get('ck3xbl_st_levy_excess') === 50 && of(1).get('ck3xbl_st_default') === 0, 'ST|FIX of army A');
  assert(of(2).get('ck3xbl_st_levy_excess') === 200 && of(2).get('ck3xbl_st_default') === 1, 'ST|FIX of army B');
  assert(of(3).get('ck3xbl_st_unindexed') === D.regiments[0].cur && of(3).get('ck3xbl_after') === 550 + D.regiments[0].cur, 'ST|FIX of army D');
  const done = stRecords(sim, 'DONE');
  assert(done.length === 1 && done[0].get('ck3xbl_st_reason') === 3 && done[0].get('ck3xbl_bid') === CLOCK_BID + 1, 'ST|DONE');
  assert(sim.list('ck3xbl_armies').length === 0 && w.armies.every((a) => a.vars.size === 0), 'the fix left an army tagged or a variable on it');
  assert(sim.list('ck3xbl_st_chars').length === 0 && w.armies.every((a) => !a.owner.modifiers.size && !a.commander.modifiers.size), `a steering modifier is left: ${sim.list('ck3xbl_st_chars').map((x) => x.name)} | ${w.armies.map((a) => [...a.owner.modifiers, ...a.commander.modifiers, ...a.owner.vars.keys(), ...a.commander.vars.keys()].join('/'))}`);
  assert(sim.events.some((e) => e.id === 'ck3xbl_clock.0003' && e.root === player), 'the summary opener did not follow');
});

test('clock.apply_to_a_finished_combat_sets_the_end_state_at_once', () => {
  const w = clockWorld({ sides: [[1000], [800]] });
  const { sim, player } = w;
  for (const a of w.armies) a.side = null;
  sim.advance(20);
  applyV4(w, { outcome: 2, targets: [100, 300], live: false, slots: [{ tag: 1, levy: 700 }] });
  assert(w.armies[0].levies === 700, 'the gone combat\'s army did not get its end state');
  const done = stRecords(sim, 'DONE');
  assert(done.length === 1 && done[0].get('ck3xbl_st_reason') === 4, 'no ST|DONE reason 4');
  assert(varOf(player, 'ck3xbl_applied_steer') === 2, 'event 0002 would open a second summary');
  assert(stRecords(sim, 'BEGIN').length === 0, 'a gone combat began steering');
  // No army of the battle is left at all: ST|DONE reason 4, and 0002 opens the summary itself.
  const w2 = clockWorld({ sides: [[1000], [800]] });
  w2.sim.lists.set('ck3xbl_armies', []);
  applyV4(w2, { outcome: 2, targets: [100, 300], live: false });
  assert(stRecords(w2.sim, 'DONE').length === 1 && !w2.player.vars.has('ck3xbl_applied_steer'), 'no army left: ST|DONE or the summary');
  // Too late to steer (combat day 13 or later): the end state is set at once, and the combat goes back to CK3 untagged.
  const w3 = clockWorld({ sides: [[1000], [800]] });
  w3.sim.advance(HARD_END_DAY);
  applyV4(w3, { outcome: 1, targets: [100, 300], slots: [{ tag: 2, levy: 500 }] });
  assert(w3.armies[1].levies === 500 && stRecords(w3.sim, 'DONE')[0].get('ck3xbl_st_reason') === 4, 'too late: end state or reason');
  assert(!w3.c.vars.has('ck3xbl_st_bid') && !w3.c.att.vars.has('ck3xbl_st_role'), 'too late: the combat is still steered');
  assert(w3.armies.every((a) => varOf(a, 'ck3xbl_bid') === CLOCK_BID), 'too late: the armies lost the tags their native END releases');
});

const globalOf = (sim, name) => sim.globals.get(name)?.value;
/** The text of every script file of the mod (.txt, hand-written and generated). */
function listModTexts(dir = MOD) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? listModTexts(path.join(dir, e.name))
    : e.name.endsWith('.txt') ? [fs.readFileSync(path.join(dir, e.name), 'utf8')] : []));
}
const pulsesOf = (sim) => sim.events.filter((e) => e.id === 'ck3xbl_clock.0001');

/** A steered battle (outcome 1, bid CLOCK_BID) whose loser side was reinforced on day 6: the state ck3xbl_st_suspend_effect and the
 * child's snapshot leave (the combat suspended for the child, every army tagged with the child's bid, the modifiers off). */
function suspendedWorld({ reinforcement = 0 } = {}) {
  const w = clockWorld({ sides: [[2000], [2000]] });
  const { sim, c, player } = w;
  const child = CLOCK_BID + 1;
  sim.advance(1);
  applyV4(w, { outcome: 1, targets: [800, 300] });
  sim.advance(6);
  const armies = [...w.armies];
  if (reinforcement) {
    const owner = w.armies[1].owner;
    const r = army('reinforcement', { owner, commander: character('reinforcement commander'), levies: reinforcement });
    joinCombat(r, c.def);
    sim.setVar(r.vars, 'ck3xbl_aid', 99);
    sim.setVar(r.vars, 'ck3xbl_aside', 2);
    sim.list('ck3xbl_armies').push(r);
    armies.push(r);
    w.reinforcement = r;
  }
  for (const x of [c, ...armies]) sim.setVar(x.vars, 'ck3xbl_bid', child);
  sim.setVar(c.vars, 'ck3xbl_st_suspended', 1);
  sim.setVar(c.vars, 'ck3xbl_st_susp_days', 0);
  sim.setVar(c.vars, 'ck3xbl_st_child', child);
  for (const e of ['ck3xbl_st_remove_owner_levers_effect', 'ck3xbl_st_remove_hard_effect']) sim.call(e, { root: player, saved: new Map([['ck3xbl_st_cur_bid', CLOCK_BID]]) });
  return { ...w, child };
}

const pulseOn = (w, day) => {
  w.sim.advance(day);
  w.sim.call('ck3xbl_bump_global_counter_effect', { root: w.player, params: { NAME: 'ck3xbl_clock_tick' } });
  w.sim.call('ck3xbl_st_pulse_tick_effect', { root: w.player, scope: w.c });
};

test('clock.player_killed_in_the_apply_rearms_on_the_heir', () => {
  // Bannerlord killed the player (fate 4 runs before the steering starts) and CK3 already plays the heir: the chain goes to him.
  {
    const w = clockWorld({ sides: [[1000], [1000]] });
    const heir = character('heir');
    w.player.alive = false;
    w.sim.player = heir;
    w.sim.advance(1);
    applyV4(w, { outcome: 1, targets: [300, 100] });
    const p = pulsesOf(w.sim);
    assert(p.length === 1 && p[0].root === heir && p[0].days === 1 && p[0].saved.get('ck3xbl_chain') === globalOf(w.sim, 'ck3xbl_clock_chain'),
      `heir already played: pulses ${p.map((e) => e.root.name)}`);
    assert(globalOf(w.sim, 'ck3xbl_clock_alive') === 1 && globalOf(w.sim, 'ck3xbl_clock_running') === 1, 'heir already played: no heartbeat');
  }
  // CK3 still has the dead player: no pulse that would never fire and no heartbeat, so the player change (running = 1) or the next
  // army move (no heartbeat) re-arms the chain at once.
  {
    const w = clockWorld({ sides: [[1000], [1000]] });
    w.player.alive = false;
    w.sim.advance(1);
    applyV4(w, { outcome: 1, targets: [300, 100] });
    assert(pulsesOf(w.sim).length === 0, 'dead player: a pulse was armed on him');
    assert(!w.sim.globals.has('ck3xbl_clock_alive') && globalOf(w.sim, 'ck3xbl_clock_running') === 1, 'dead player: heartbeat set or chain not running');
    const heir = character('heir');
    w.sim.player = heir;
    w.sim.call('ck3xbl_st_ensure_chain_effect', { root: heir });
    const p = pulsesOf(w.sim);
    assert(p.length === 1 && p[0].root === heir && globalOf(w.sim, 'ck3xbl_clock_alive') === 1, 'dead player: the next army move did not re-arm on the heir');
  }
});

test('clock.resume_steers_the_reinforcement_and_frees_its_owner', () => {
  // One ruler owns the loser's army and the reinforcement that opened the second clash. The offer is declined: the parent
  // resumes in hand-back mode, the reinforcement fights on as a joiner, and its owner keeps his levers.
  const w = suspendedWorld({ reinforcement: 800 });
  const { sim, c, player, reinforcement: r } = w;
  const owner = r.owner;
  sim.call('ck3xbl_st_resume_effect', { root: player, scope: c, params: { MODE: 2, REASON: 2 }, saved: new Map([['ck3xbl_bid', w.child]]) });
  const resume = stRecords(sim, 'RESUME');
  assert(resume.length === 1 && resume[0].get('ck3xbl_st_reason') === 2 && resume[0].get('ck3xbl_st_mode') === 2, 'no ST|RESUME mode 2 reason 2');
  assert(varOf(r, 'ck3xbl_st_bid') === CLOCK_BID && varOf(r, 'ck3xbl_st_joiner') === 1, 'the reinforcement is not a steered joiner');
  assert(!w.armies.some((a) => a.vars.has('ck3xbl_st_joiner')), 'a steered army became a joiner');
  pulseOn(w, 7);
  assert(varOf(owner, 'ck3xbl_st_lev_bid') === CLOCK_BID, 'the owner of the reinforcement carries no levers after ST|RESUME');
  assert(r.commander.modifiers.has('ck3xbl_st_hard'), 'the reinforcement\'s commander has no hard modifier');
  // The fix releases the reinforcement with the battle.
  for (const a of c.location.armies) a.side = null;
  sim.call('ck3xbl_st_fix_effect', { root: player, saved: new Map([['ck3xbl_st_cur_bid', CLOCK_BID]]) });
  assert(!sim.list('ck3xbl_armies').includes(r) && r.vars.size === 0, `the fix left the reinforcement tagged: ${[...r.vars.keys()]}`);
  assert(!owner.modifiers.size && !r.commander.modifiers.size, 'the fix left a steering modifier');
});

test('clock.stale_second_clash_does_not_hold_the_parent', () => {
  // The child was taken to the field (CHOICE|FIELD); the player who chose it is dead (stale) or alive (pending).
  for (const stale of [true, false]) {
    const w = suspendedWorld();
    const { sim, c, child } = w;
    const fielder = character('fielder');
    fielder.alive = !stale;
    sim.setVar(sim.globals, 'ck3xbl_pending_bid', child);
    sim.setVar(sim.globals, 'ck3xbl_pending_player', fielder);
    sim.setVar(sim.globals, 'ck3xbl_pending_fresh', 1);
    for (let k = 1; k <= FAILSAFE_DAYS; k++) pulseOn(w, 6 + k);
    const resume = stRecords(sim, 'RESUME');
    const choices = sim.log.filter((l) => l.record.startsWith('CK3XBL|CHOICE|'));
    if (stale) {
      assert(resume.length === 1 && resume[0].get('ck3xbl_st_reason') === 3 && !c.vars.has('ck3xbl_st_suspended'), 'stale child: the failsafe did not resume');
      assert(choices.length === 1 && choices[0].record === 'CK3XBL|CHOICE|ABANDON' && choices[0].saved.get('ck3xbl_bid') === child,
        `stale child: ${choices.map((l) => l.record)}`);
      assert(globalOf(sim, 'ck3xbl_pending_bid') === 0 && !sim.globals.has('ck3xbl_pending_player'), 'stale child: still pending');
      assert(stRecords(sim, 'P').at(-1).get('ck3xbl_bid') === CLOCK_BID, 'stale child: the pulse lost its bid');
    } else {
      assert(resume.length === 0 && choices.length === 0 && c.vars.has('ck3xbl_st_suspended'), 'pending child: the failsafe fired');
    }
  }
  // Another battle is taken to the field while the child is stale (ck3xbl_choose_field_effect drops it): the parent resumes
  // before the cleanup, so the reinforcement becomes a steered joiner instead of losing its tag.
  {
    const w = suspendedWorld({ reinforcement: 800 });
    const { sim, child, reinforcement: r } = w;
    const fielder = character('fielder');
    fielder.alive = false;
    sim.setVar(sim.globals, 'ck3xbl_pending_bid', child);
    sim.setVar(sim.globals, 'ck3xbl_pending_player', fielder);
    const ctx = sim.call('ck3xbl_drop_stale_pending_effect', { root: w.player, saved: new Map([['ck3xbl_bid', 42]]) });
    const resume = stRecords(sim, 'RESUME');
    assert(resume.length === 1 && resume[0].get('ck3xbl_st_reason') === 3, 'dropped by another FIELD: no ST|RESUME reason 3');
    assert(varOf(r, 'ck3xbl_st_joiner') === 1 && varOf(r, 'ck3xbl_bid') === child && sim.list('ck3xbl_armies').includes(r),
      'dropped by another FIELD: the reinforcement lost its tag');
    assert(ctx.saved.get('ck3xbl_bid') === 42 && globalOf(sim, 'ck3xbl_pending_bid') === 0, 'dropped by another FIELD: the caller\'s bid or the pending state');
  }
});

test('clock.events_drop_carried_combat_and_army_scopes', () => {
  const sim = new ScriptSim(MOD);
  const c = combat('Field');
  const keep = { ck3xbl_chain: 3, ck3xbl_bid: 7, ck3xbl_enemy_leader: character('enemy'), ck3xbl_player: character('player') };
  const drop = { army: army('joiner', { owner: character('owner') }), ck3xbl_combat: c, ck3xbl_res_combat: c, ck3xbl_att_side: c.att,
    ck3xbl_def_side: c.def, ck3xbl_my_side: c.att, ck3xbl_enemy_side: c.def, ck3xbl_side: c.att, ck3xbl_res_victim_side: c.def,
    ck3xbl_war: { kind: 'war', vars: new Map() } };
  const saved = new Map(Object.entries({ ...keep, ...drop }));
  sim.call('ck3xbl_st_drop_carried_scopes_effect', { root: keep.ck3xbl_player, saved });
  assert(JSON.stringify([...saved.keys()].sort()) === JSON.stringify(Object.keys(keep).sort()), `left: ${[...saved.keys()]}`);
});

test('snapshot.c_absent_marks_characters_not_in_the_battle', () => {
  // PROTOCOL 15 (live test 2026-10-04, the stand-in without a commander): every C dump carries ck3xbl_c_absent, 1 when the character
  // is not physically in the battle. Runs the statements of ck3xbl_save_char_values_effect that set it, after ck3xbl_c_army.
  const defs = new Map(parseText(stripBom(fs.readFileSync(path.join(MOD, 'common', 'scripted_effects', 'ck3xbl_snapshot_effects.txt'), 'utf8'))).map((n) => [n.key, n]));
  const body = defs.get('ck3xbl_save_char_values_effect').block;
  const at = body.findIndex((n) => n.key === 'save_scope_value_as' && n.block.some((c) => c.key === 'name' && c.value === 'ck3xbl_c_absent'));
  const army = body.findIndex((n) => n.key === 'knight_army');
  assert(at > army && army > 0, 'ck3xbl_c_absent is not saved after the ck3xbl_c_army lookup');
  const absentOf = (saved) => {
    const sim = new ScriptSim(MOD);
    const ch = character('c');
    const ctx = { root: ch, this: ch, saved: new Map(Object.entries(saved)), params: {} };
    sim.run(body.slice(at, at + 2), ctx);
    return ctx.saved.get('ck3xbl_c_absent');
  };
  assert(absentOf({ ck3xbl_c_army: 7, ck3xbl_c_is_player: 0 }) === 0, 'a commander of a tagged army is absent');
  assert(absentOf({ ck3xbl_c_army: 0, ck3xbl_c_is_player: 0 }) === 1, 'a war leader at home is present');
  assert(absentOf({ ck3xbl_c_army: 0, ck3xbl_c_is_player: 1, ck3xbl_player_present: 0 }) === 1, 'the player at home is present');
  assert(absentOf({ ck3xbl_c_army: 0, ck3xbl_c_is_player: 1 }) === 1, 'the player of a J record without ck3xbl_player_present is present');
  assert(absentOf({ ck3xbl_c_army: 0, ck3xbl_c_is_player: 1, ck3xbl_player_present: 1 }) === 0,
    'the player with an army at the site (still tagged for the Bannerlord battle) is absent');
  // The player record of the side of his realm: C|PLAYER on both sides, after every other role of that side.
  const side = fs.readFileSync(path.join(MOD, 'common', 'scripted_effects', 'ck3xbl_gen_side_effects.txt'), 'utf8');
  for (const s of ['att', 'def']) {
    const block = side.slice(side.indexOf(`ck3xbl_emit_side_${s}_effect = {`));
    const order = ['CMD', 'KNIGHT', 'PRIMARY', 'PLAYER'].map((r) => block.indexOf(`"CK3XBL|C|${r}|${s}"`));
    assert(order.every((x, i) => x > 0 && (i === 0 || x > order[i - 1])), `${s}: the C roles are not emitted in the order CMD, KNIGHT, PRIMARY, PLAYER`);
  }
});

test('clock.dust_settles_option_defers_to_0004', () => {
  // Live test 2026-10-04: CK3 rebuilds an open option's effect tooltip every frame; walking the apply and the steering from the
  // tooltip of "The dust settles..." logged 66,626 error groups. Option a of 0002 may only trigger the hidden event 0004, and 0004
  // holds the apply and what follows it.
  const events = new Map(parseText(stripBom(fs.readFileSync(path.join(MOD, 'events', 'ck3xbl_battle_events.txt'), 'utf8'))).map((n) => [n.key, n]));
  const options = events.get('ck3xbl_battle.0002').block.filter((n) => n.key === 'option');
  const a = options.find((o) => o.block.some((n) => n.key === 'name' && n.value === 'ck3xbl_battle.0002.a'));
  assert(a, 'event 0002 has no option ck3xbl_battle.0002.a');
  const body = a.block.filter((n) => n.key !== 'name');
  assert(body.length === 1 && body[0].key === 'hidden_effect', `option a holds ${body.map((n) => n.key).join(', ')}, not one hidden_effect`);
  const inner = body[0].block;
  assert(inner.length === 1 && inner[0].key === 'trigger_event' && inner[0].value === 'ck3xbl_battle.0004',
    `option a's hidden_effect holds ${inner.map((n) => `${n.key} = ${n.value ?? '{...}'}`).join(', ')}, not only trigger_event = ck3xbl_battle.0004`);
  const e4 = events.get('ck3xbl_battle.0004');
  assert(e4, 'event ck3xbl_battle.0004 is missing');
  assert(e4.block.some((n) => n.key === 'hidden' && n.value === 'yes'), 'event 0004 is not hidden');
  const effects = [];
  walk(e4.block.find((n) => n.key === 'immediate').block, (n) => effects.push(n.key));
  for (const key of ['ck3xbl_apply_effect', 'send_interface_toast', 'trigger_event']) assert(effects.includes(key), `event 0004 does not run ${key}`);
});

test('clock.pulse_dumps_carry_only_the_clock_keys', () => {
  // Live test 2026-10-04 (issue 5): every ST|P repeated about 100 saved scopes of the snapshot, because the first pulse inherits
  // the apply's event chain. The pulse event (its immediate block as written) now starts by dropping all of them but its chain id.
  const events = new Map(parseText(stripBom(fs.readFileSync(path.join(MOD, 'events', 'ck3xbl_clock_events.txt'), 'utf8'))).map((n) => [n.key, n]));
  const immediate = events.get('ck3xbl_clock.0001').block.find((n) => n.key === 'immediate').block;
  assert(immediate[0].key === 'ck3xbl_st_clear_carried_scopes_effect', `the pulse starts with ${immediate[0].key}`);
  const w = clockWorld({ sides: [[2000], [1500]] });
  const { sim, player, c } = w;
  sim.advance(1);
  applyV4(w, { outcome: 1, targets: [900, 300] });
  const pulse = pulsesOf(sim).at(-1);
  // What the apply's event chain carries on top (every name the mod saves, as the snapshot and the apply leave them).
  const carried = new Map(pulse.saved);
  const modText = listModTexts();
  for (const name of savedScopeNames(modText)) if (!carried.has(name)) carried.set(name, name.startsWith('ck3xbl_') ? 1 : c);
  carried.set('army', w.armies[0]);
  const before = carried.size;
  for (let day = 2; day <= 6; day++) {
    sim.advance(day);
    const ctx = { root: player, this: player, saved: day === 2 ? carried : new Map(pulsesOf(sim).at(-1).saved), params: {} };
    sim.run(immediate, ctx);
  }
  const dumps = stRecords(sim, 'P');
  assert(dumps.length === 5, `${dumps.length} ST|P records over five pulses`);
  for (const d of dumps) {
    const foreign = [...d.keys()].filter((k) => !k.startsWith('ck3xbl_st_') && !['ck3xbl_bid', 'ck3xbl_chain'].includes(k));
    assert(foreign.length === 0, `an ST|P dump carries ${foreign.join(', ')} (the pulse inherited ${before} saved scopes)`);
    for (const key of ['ck3xbl_bid', 'ck3xbl_st_cday', 'ck3xbl_st_cday_marks', 'ck3xbl_st_days', 'ck3xbl_st_att_killed', 'ck3xbl_st_def_killed',
      'ck3xbl_st_att_target', 'ck3xbl_st_def_target', 'ck3xbl_st_att_men', 'ck3xbl_st_def_men', 'ck3xbl_st_att_gain', 'ck3xbl_st_def_gain', 'ck3xbl_st_susp']) {
      assert(d.has(key), `an ST|P dump lacks ${key} (PROTOCOL 14.5)`);
    }
    assert(d.size <= 40, `an ST|P dump has ${d.size} keys`);
    if (process.env.CK3XBL_CLOCK_TRACE) console.log(`ST|P keys: ${d.size}`);
  }
  assert(dumps.at(-1).get('ck3xbl_bid') === CLOCK_BID && pulsesOf(sim).at(-1).saved.get('ck3xbl_chain') === globalOf(sim, 'ck3xbl_clock_chain'),
    'the pulse lost its bid or its chain id');
});

test('clock.summary_window_keeps_its_numbers', () => {
  const w = clockWorld({ sides: [[1000], [1000]] });
  const { sim, player } = w;
  sim.advance(1);
  // Battle A's summary window is open (ck3xbl_clock.0003 wrote its numbers).
  const shown = { outcome: 1, my_lost: 111, enemy_lost: 222, my_kills: 3, won: 1 };
  for (const [k, v] of Object.entries(shown)) sim.setVar(player.vars, `ck3xbl_res_${k}`, v);
  sim.setVar(player.vars, 'ck3xbl_res_showing', 1, 30);
  const playerRes = () => Object.fromEntries(Object.keys(shown).map((k) => [k, varOf(player, `ck3xbl_res_${k}`)]));
  // Battle B's payload 4 apply.
  const summary = (outcome, version = REG.steerPayloadVersion) => {
    for (let i = 0; i < REG.count; i++) sim.setVar(player.vars, `ck3xbl_r${i}`, 0);
    sim.setVar(player.vars, `ck3xbl_r${REG.version}`, version);
    sim.setVar(player.vars, `ck3xbl_r${REG.summary.myLost}`, 500);
    sim.setVar(player.vars, `ck3xbl_r${REG.summary.enemyLost}`, 700);
    sim.setVar(player.vars, `ck3xbl_r${REG.summary.myKills}`, 4);
    return sim.call('ck3xbl_apply_summary_effect', { root: player,
      saved: new Map([['ck3xbl_res_outcome', outcome], ['ck3xbl_res_winner_code', 2], ['ck3xbl_res_my_side', 2]]) });
  };
  const ctx = summary(2);
  assert(JSON.stringify(playerRes()) === JSON.stringify(shown), `the open window's numbers changed: ${JSON.stringify(playerRes())}`);
  const b = Object.fromEntries(['my_lost', 'enemy_lost', 'my_kills', 'won'].map((k) => [k, ctx.saved.get(`ck3xbl_res_${k}`)]));
  assert(JSON.stringify(b) === JSON.stringify({ my_lost: 500, enemy_lost: 700, my_kills: 4, won: 1 }), `summary values: ${JSON.stringify(b)}`);
  applyV4(w, { outcome: 2, targets: [100, 300], summary: b });
  assert(w.armies.every((a) => varOf(a, 'ck3xbl_st_res_my_lost') === 500 && varOf(a, 'ck3xbl_st_res_enemy_lost') === 700 &&
    varOf(a, 'ck3xbl_st_res_my_kills') === 4 && varOf(a, 'ck3xbl_st_res_won') === 1 && varOf(a, 'ck3xbl_st_res_outcome') === 2),
  'the steered armies do not carry battle B\'s summary');
  // Windows that open at once from the player variables still get them: a cancel, a payload 3, or no window open.
  summary(4);
  assert(varOf(player, 'ck3xbl_res_outcome') === 4 && varOf(player, 'ck3xbl_res_my_lost') === 500, 'a cancel did not write its summary');
  for (const [k, v] of Object.entries(shown)) sim.setVar(player.vars, `ck3xbl_res_${k}`, v);
  summary(2, 3);
  assert(varOf(player, 'ck3xbl_res_my_lost') === 500, 'a payload 3 did not write its summary');
  for (const [k, v] of Object.entries(shown)) sim.setVar(player.vars, `ck3xbl_res_${k}`, v);
  player.vars.delete('ck3xbl_res_showing');
  summary(2);
  assert(varOf(player, 'ck3xbl_res_my_lost') === 500 && varOf(player, 'ck3xbl_res_outcome') === 2, 'no window open: the summary was not written');
});

// --- knights and commanders: generated records (PROTOCOL 13.2) --------------------------------------------------

const KNIGHT_EFFECTS = 'common/scripted_effects/ck3xbl_gen_knight_effects.txt';
const SIDE_EFFECTS = 'common/scripted_effects/ck3xbl_gen_side_effects.txt';
const APPLY_EFFECTS = 'common/scripted_effects/ck3xbl_gen_apply_effects.txt';
const KE_DRAFT = path.join(REPO, 'tools', 'gen', 'knights', 'draft', 'ck3xbl_knight_eff_effects.txt');
const readData = (name) => JSON.parse(stripBom(fs.readFileSync(path.join(REPO, 'bridge', 'data', name), 'utf8')));

/** The records (text after CK3XBL|) of every info_log under `nodes`. */
function recordsIn(nodes) {
  const records = [];
  walk(nodes, (n) => n.key === 'info_log' && records.push(n.value.replace(/^CK3XBL\|/, '')));
  return records;
}

/** Text of the top-level block `name = { ... }` of a mod file, as written (the generated files hold no braces in strings). */
function blockText(rel, name) {
  const src = stripBom(fs.readFileSync(path.join(MOD, ...rel.split('/')), 'utf8'));
  const at = src.indexOf(`\n${name} = {`) + 1;
  if (at === 0) throw new Error(`${name} not found in ${rel}`);
  let depth = 0;
  for (let i = src.indexOf('{', at); i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}' && --depth === 0) return src.slice(at, i + 1);
  }
  throw new Error(`${name} is not closed in ${rel}`);
}

test('knights.records_cover_the_emit_plan', () => {
  const combat = readData('ck3_combat_traits.json');
  const plan = combat.emitPlan;
  const defs = defsOf(KNIGHT_EFFECTS);
  const of = (effect, prefix) => recordsIn(defs.get(effect).block).filter((r) => r.startsWith(prefix)).map((r) => r.slice(prefix.length));
  const same = (got, want, label) => assert(JSON.stringify([...got].sort()) === JSON.stringify([...want].sort()),
    `${label}: generated ${got.length}, expected ${want.length} (${want.filter((w) => !got.includes(w)).slice(0, 5).join(', ')} missing)`);

  same(of('ck3xbl_emit_cx_combat_effect', 'CX|ktrait|'), plan.traitsNotEmittedToday, 'CX|ktrait');
  const levels = plan.traitsWithLevels.flatMap((k) => combat.traits[k].emit.levels.flatMap((t) => t.chain.map((c) => t.record.replace('<level>', c.level))));
  same(of('ck3xbl_emit_cx_combat_effect', '').filter((r) => r.startsWith('CX|txp|')), levels, 'CX|txp');
  assert(levels.some((r) => r === 'CX|txp|the_wake|4') && levels.some((r) => r === 'CX|txp|tourney_participant:foot|3'), 'the 4-level and tracked chains');
  same(of('ck3xbl_emit_cx_combat_effect', 'CX|cparam|'), plan.cultureParameters, 'CX|cparam');
  same(of('ck3xbl_emit_cx_combat_effect', 'CX|fparam|'), plan.faithParameters, 'CX|fparam');
  const owners = Object.keys(combat.traits).filter((k) => combat.traits[k].roles.includes('armyOwner'));
  assert(owners.length === 25, `${owners.length} army-owner traits, the design counts 25`);
  same(of('ck3xbl_emit_ax_owner_combat_effect', 'AX|otrait|'), owners, 'AX|otrait');
  same(of('ck3xbl_emit_ax_owner_combat_effect', 'AX|ocparam|'), plan.cultureParameters, 'AX|ocparam');
  same(of('ck3xbl_emit_ax_owner_combat_effect', 'AX|ofparam|'), plan.faithParameters, 'AX|ofparam');
  same(of('ck3xbl_emit_ax_army_effect', 'AX|'), ['besieging|yes', 'besieging|no', 'territory|own', 'territory|hostile', 'territory|other'], 'AX army');

  const accolades = Object.keys(readData('ck3_accolade_types.json').types);
  same(of('ck3xbl_emit_accolade_effect', 'CX|acc_primary|'), accolades, 'CX|acc_primary');
  same(of('ck3xbl_emit_accolade_effect', 'CX|acc_type|'), accolades.flatMap((t) => [3, 2, 1, 0].map((l) => `${t}|${l}`)), 'CX|acc_type');
  same(of('ck3xbl_emit_accolade_effect', 'CX|acc_rank|'), ['6', '5', '4', '3', '2', '1', '0'], 'CX|acc_rank');
  const artifacts = of('ck3xbl_emit_cx_artifacts_effect', 'CX|artifact');
  assert(artifacts.includes('|primary_armament|sword|famed') && artifacts.includes('|armor|armor_mail|illustrious') &&
    artifacts.includes('_helmet|armor_helmet_nasal'), 'artifact records for a famed sword and an illustrious mail with a nasal helmet');
  assert(!recordsIn([...defs.values()]).some((r) => r.startsWith('AX|ke_art')), 'AX|ke_art is not logged (artifact knight effectiveness is inside ck3xbl_a_ke)');
});

test('knights.ke_effect_is_the_draft_plus_cleared_helpers', () => {
  const sources = readData('ck3_knight_effectiveness_sources.json').sources;
  const mod = blockText(KNIGHT_EFFECTS, 'ck3xbl_save_knight_eff_effect');
  assert(mod === knightEffEffectLines(sources, { clearHelpers: true }).join('\n'), 'the mod effect is not the renderer output');
  const draft = stripBom(fs.readFileSync(KE_DRAFT, 'utf8'));
  const draftEffect = draft.slice(draft.indexOf('ck3xbl_save_knight_eff_effect = {')).trimEnd();
  const clears = ['ck3xbl_kl', 'holder', 'councillor_liege'].map((n) => `\tclear_saved_scope = ${n}`).join('\n');
  assert(mod === draftEffect.replace(/\n\}$/, `\n${clears}\n}`), 'the mod effect differs from the draft beyond clearing its helper scopes');
});

test('knights.helper_scopes_cleared_where_they_are_saved', () => {
  // Saved scopes, temporary ones included, are printed by every later info_log_scopes dump of the snapshot (in game,
  // 2026-10-04), and clear_saved_scope removes them. Each helper is saved with save_scope_as and cleared, after its last read,
  // in the statement list that saves it (a one-line scope switch such as faith = { save_scope_as = x } counts as saving).
  const helpers = [
    [KNIGHT_EFFECTS, 'ck3xbl_save_knight_eff_effect', ['ck3xbl_kl', 'holder', 'councillor_liege']],
    [KNIGHT_EFFECTS, 'ck3xbl_emit_ax_army_effect', ['ck3xbl_ax_owner']],
    [KNIGHT_EFFECTS, 'ck3xbl_emit_accolade_effect', ['ck3xbl_acc_knight']],
    [SIDE_EFFECTS, 'ck3xbl_emit_side_att_effect', ['ck3xbl_s_my_faith', 'ck3xbl_s_enemy_faith']],
    [SIDE_EFFECTS, 'ck3xbl_emit_side_def_effect', ['ck3xbl_s_my_faith', 'ck3xbl_s_enemy_faith']],
  ];
  const isSave = (n, name) => (n.key === 'save_scope_as' || n.key === 'save_temporary_scope_as') && n.value === name;
  for (const [rel, effect, names] of helpers) {
    for (const name of names) {
      let list = null;
      walk([defsOf(rel).get(effect)], (n) => {
        const at = (n.block || []).findIndex((c) => isSave(c, name) || (c.block?.length === 1 && isSave(c.block[0], name)));
        if (at >= 0 && !list) list = n.block; // the outermost list: a one-line scope switch is its own list too
      });
      assert(list, `${effect}: ${name} is never saved`);
      const save = list.map((c) => (isSave(c, name) ? c : c.block?.[0])).find((c) => c && isSave(c, name));
      assert(save.key === 'save_scope_as', `${effect}: ${name} is saved with ${save.key}`);
      const clearAt = list.findIndex((c) => c.key === 'clear_saved_scope' && c.value === name);
      assert(clearAt >= 0, `${effect}: ${name} is not cleared in the block that saves it`);
      const lastRead = list.findLastIndex((c) => JSON.stringify(c).includes(`scope:${name}`));
      assert(lastRead < clearAt, `${effect}: ${name} is read after it is cleared`);
    }
  }
});

test('knights.ke_saved_before_and_cleared_after_every_army_dump', () => {
  // Every list of statements that holds an A or J marker (directly or in an if/else chain): the owner's knight effectiveness
  // is saved before it, and right after the chain come the dump, the clear and the army-scope AX lines.
  const isArmyMarker = (n) => (n.key === 'info_log' || n.key === 'if') && recordsIn([n]).some((r) => /^(A|J)\|(att|def)$/.test(r));
  const checked = [];
  for (const rel of [SIDE_EFFECTS, SNAP]) {
    walk(parseText(stripBom(modText(rel))), (node) => {
      if (!node.block || ['if', 'else_if', 'else'].includes(node.key)) return; // a J marker's branch: its chain is checked
      const list = node.block;
      const at = list.findIndex(isArmyMarker);
      if (at < 0) return;
      let end = at;
      while (list[end + 1] && (list[end + 1].key === 'else_if' || list[end + 1].key === 'else')) end++;
      const prev = list[at - 1];
      assert(prev?.key === 'army_owner' && prev.block.length === 1 && prev.block[0].key === 'ck3xbl_save_knight_eff_effect',
        `${rel}:${list[at].line}: knight effectiveness is not saved right before the marker`);
      const after = list.slice(end + 1, end + 4).map((n) => `${n.key} = ${n.value}`);
      assert(JSON.stringify(after) === JSON.stringify(['info_log_scopes = yes', 'clear_saved_scope = ck3xbl_a_ke', 'ck3xbl_emit_ax_army_effect = yes']),
        `${rel}:${list[at].line}: after the marker come ${after.join(', ')}`);
      checked.push(`${rel}:${list[at].line}`);
    });
  }
  assert(checked.length === 3, `A/J records checked: ${checked.join(', ')} (expected A|att, A|def and J)`);
});

test('knights.side_records_log_commander_and_faith_hostility', () => {
  for (const effect of ['ck3xbl_emit_side_att_effect', 'ck3xbl_emit_side_def_effect']) {
    const side = defsOf(SIDE_EFFECTS).get(effect).block[0].block;
    const keys = side.map((n) => (n.key === 'save_scope_value_as' ? field(n, 'name').value : n.key));
    const marker = side.findIndex((n) => n.key === 'info_log');
    for (const k of ['ck3xbl_s_has_cmd', 'ck3xbl_s_hostility_out', 'side_commander']) {
      const i = keys.indexOf(k);
      assert(i >= 0 && i < marker, `${effect}: ${k} is not saved before the S marker`);
    }
    const commander = side.find((n) => n.key === 'side_commander');
    assert(commander.op === '?=', `${effect}: side_commander must be optional`);
    assert(JSON.stringify(commander).includes('ck3xbl_s_cmd'), `${effect}: side_commander does not save ck3xbl_s_cmd`);
    // side_commander is empty at on_combat_start (in game, 2026-10-04): the commander of the side's largest army stands in, for
    // the S keys and for the enemy commander of the faith hostility.
    const ifs = side.filter((n) => n.key === 'if');
    const blocks = ifs.map((n) => JSON.stringify(n));
    const limitOf = (n) => field(n, 'limit').block.map((c) => `${c.key} ${c.op} ${c.value}`).join(' ');
    const fallback = JSON.stringify(ifs.find((n) => limitOf(n) === 'scope:ck3xbl_s_has_cmd = 0'));
    assert(fallback && fallback.includes('ordered_army_in_location') && fallback.includes('army_size') && fallback.includes('ck3xbl_s_cmd'),
      `${effect}: no fallback to the commander of the largest army`);
    const hostility = blocks.find((t) => t.includes('faith_hostility_level'));
    for (const name of ['ck3xbl_s_my_faith', 'ck3xbl_s_enemy_faith', 'ck3xbl_s_enemy_cmd', 'ordered_army_in_location']) {
      assert(hostility?.includes(name), `${effect}: the faith hostility has no ${name}`);
    }
  }
});

test('knights.side_commander_falls_back_to_the_largest_army', () => {
  // Interpreted: no side commander in the engine's lists, so each side's commander is its largest army's, and the enemy's too.
  const sim = new ScriptSim(MOD, { stubs: {} });
  const c = combat('Field');
  const cmd = (name) => character(name);
  const small = army('small', { owner: character('o1'), commander: cmd('small commander'), levies: 300 });
  const big = army('big', { owner: character('o2'), commander: cmd('big commander'), levies: 900 });
  const enemy = army('enemy', { owner: character('o3'), commander: cmd('enemy commander'), levies: 500 });
  joinCombat(small, c.att);
  joinCombat(big, c.att);
  joinCombat(enemy, c.def);
  const side = defsOf(SIDE_EFFECTS).get('ck3xbl_emit_side_att_effect').block[0].block;
  const commanderPart = side.slice(side.findIndex((n) => n.key === 'save_scope_value_as' && field(n, 'name').value === 'ck3xbl_s_has_cmd'),
    side.findIndex((n) => n.key === 'info_log'));
  const withoutFaith = commanderPart.filter((n) => !(n.key === 'if' && JSON.stringify(n).includes('faith_hostility_level')));
  const ctx = { root: c, this: c.att, saved: new Map([['ck3xbl_side', c.att]]), params: {} };
  sim.run(withoutFaith, ctx);
  assert(ctx.saved.get('ck3xbl_s_has_cmd') === 1 && ctx.saved.get('ck3xbl_s_cmd') === big.commander, 'the attacker S record has no fallback commander');
  c.att.commanders = [small.commander];
  const ctx2 = { root: c, this: c.att, saved: new Map([['ck3xbl_side', c.att]]), params: {} };
  sim.run(withoutFaith, ctx2);
  assert(ctx2.saved.get('ck3xbl_s_cmd') === small.commander, 'a side_commander set by the engine is not preferred');
});

test('knights.txp_chains_with_track_and_four_levels_lint_clean', () => {
  const combat = defsOf(KNIGHT_EFFECTS).get('ck3xbl_emit_cx_combat_effect').block;
  // The leveled block of a trait (not its one-line CX|ktrait if): `if = { limit = { has_trait = <trait> } <chains> }`.
  const chainOf = (trait) => combat.find((n) => n.key === 'if' && field(n, 'limit').block[0].value === trait && n.block.some((c) => c.key === 'if'));
  const tourney = chainOf('tourney_participant');
  const wake = chainOf('the_wake');
  const foot = recordsIn(tourney.block).filter((r) => r.startsWith('CX|txp|tourney_participant:foot|'));
  assert(JSON.stringify(foot) === JSON.stringify(['3', '2', '1'].map((l) => `CX|txp|tourney_participant:foot|${l}`)), `tourney foot chain ${foot}`);
  let footTrack = 0;
  walk(tourney.block, (n) => n.key === 'track' && n.value === 'foot' && footTrack++);
  assert(footTrack === 3, `the tourney foot chain names its track ${footTrack} times, expected 3`);
  const wakeLevels = recordsIn(wake.block);
  assert(JSON.stringify(wakeLevels) === JSON.stringify(['4', '3', '2', '1'].map((l) => `CX|txp|the_wake|${l}`)), `the_wake chain ${wakeLevels}`);
  const thresholds = [];
  walk(wake.block, (n) => n.key === 'value' && n.op === '>=' && thresholds.push(Number(n.value)));
  assert(JSON.stringify(thresholds) === JSON.stringify([100, 75, 50, 25]), `the_wake thresholds ${thresholds}`);

  // The two chains alone in an effect: clean in character scope, reported in army scope.
  const src = blockText(KNIGHT_EFFECTS, 'ck3xbl_emit_cx_combat_effect').split('\n');
  const chainText = (trait) => {
    const start = src.findIndex((l) => l === `\t\tlimit = { has_trait = ${trait} }`) - 1;
    const end = src.findIndex((l, i) => i > start && l === '\t}');
    return src.slice(start, end + 1).join('\n');
  };
  const file = `ck3xbl_test_txp_effect = {\n${chainText('tourney_participant')}\n${chainText('the_wake')}\n}\n`;
  const lint = (anchor) => lintMod({ overrides: { 'common/scripted_effects/ck3xbl_test_txp_effects.txt': file, ...insertAfter(KNIGHT_EFFECTS, anchor, '\tck3xbl_test_txp_effect = yes') } })
    .problems.filter((p) => p.level === 'error').map((p) => p.message);
  const clean = lint('ck3xbl_emit_cx_combat_effect = {');
  assert(clean.length === 0, `the chains in character scope:\n${clean.join('\n')}`);
  assert(lint('ck3xbl_emit_ax_army_effect = {').some((m) => m.includes("'has_trait_xp' is not valid in army scope")), 'has_trait_xp in army scope is not reported');
});

// --- award slots (PROTOCOL 13.5) -------------------------------------------------------------------------------

// The award reference vectors: the encoder vector (two awards in slots 0 and 1, what the bridge's encoder produces) and the
// register vector (both blocks, fed to the mod as registers).
const AWARD_HEADER = { 1: 3, 2: 2, 3: 8199, 4: 300, 5: 900, 6: 7, 10: 12, 11: 500, 12: 48, 13: 400, 130: 5, 131: 4, 132: 2 };
const ENCODER_VECTOR = { ...AWARD_HEADER, 122: 7, 123: 13062, 124: 9, 125: 40203 };
const REGISTER_VECTOR = { ...AWARD_HEADER, 122: 7, 123: 13062, 190: 9, 191: 40203 };
const ENCODER_KEYS = 'F3 3 F1 3 F1 2 F1 8 1 9 9 F1 3 0 0 F1 9 0 0 F1 7 F1 1 0 F5 1 2 F1 5 0 0 F1 4 8 F1 4 0 0 F1 1 2 2 F5 7 F1 1 3 0 6 2 F1 9 F1 ' +
  '4 0 2 0 3 F1 1 3 0 F5 5 F1 4 F1 2 F1 1 9 9 F5 4 0 1 6 F1 F2';

test('award.reference_vectors_commit', () => {
  for (const [label, values, checksum, keys] of [['encoder', ENCODER_VECTOR, 4016, 79], ['register', REGISTER_VECTOR, 4656, 83]]) {
    const enc = encode(values);
    assert(enc.checksum === checksum, `${label} vector: r199 = ${enc.checksum}, expected ${checksum}`);
    assert(enc.keys.length === keys, `${label} vector: ${enc.keys.length} keys, expected ${keys}`);
    const sim = typeKeys(freshSim(3), enc.keys);
    assert(lastRecord(sim) === 'CK3XBL|IN|OK', `${label} vector: ${lastRecord(sim)}`);
    assert(registersOf(sim).every((v, i) => v === enc.registers[i]), `${label} vector: registers differ after the commit`);
  }
  assert(encode(ENCODER_VECTOR).keys.join(' ') === ENCODER_KEYS, 'the encoder vector keys differ from the reference');
});

test('award.extra_store_key_in_award_block_rejected', () => {
  const awardRegs = new Set([...Array(AWARD_SLOTS).keys()].flatMap((k) => Object.values(awardSlotRegisters(k))));
  const enc = encode(REGISTER_VECTOR);
  let idx = 0;
  let acc = '';
  let shifted = 0;
  enc.keys.forEach((k, at) => {
    if (/^\d$/.test(k)) acc += k;
    else if (k === 'F5') [idx, acc] = [Number(acc), ''];
    else if (k === 'F1') {
      const stored = idx;
      [idx, acc] = [idx + 1, ''];
      if (!awardRegs.has(stored)) return;
      const sim = typeKeys(freshSim(3), [...enc.keys.slice(0, at + 1), 'F1', ...enc.keys.slice(at + 1)]);
      if (registersOf(sim).every((v, i) => v === enc.registers[i])) return; // an extra 0 into a register that is 0 anyway
      shifted++;
      assert(lastRecord(sim) === 'CK3XBL|IN|BAD', `an extra F1 after the store of r${stored} shifted the award block and was accepted`);
    }
  });
  assert(shifted >= 2, `only ${shifted} shifting cases in the award blocks`);
});

test('award.value_decodes_to_prestige_glory_rolls', () => {
  const sets = defsOf(APPLY_EFFECTS).get('ck3xbl_apply_award_slot_effect').block[0].block.filter((n) => n.key === 'set_variable');
  assert(sets.length === 3, `${sets.length} set_variable lines, expected 3`);
  for (const [award, want] of [[13062, [13, 6, 2]], [40203, [40, 20, 3]], [1, [0, 0, 1]]]) {
    const sim = new KeypadSim();
    sim.saved = new Map();
    sim.vars.set('ck3xbl_r123', award);
    sim.run(sets, { TAG: 'ck3xbl_r122', AWARD: 'ck3xbl_r123' });
    const got = ['ck3xbl_aw_p', 'ck3xbl_aw_g', 'ck3xbl_aw_r'].map((v) => sim.vars.get(v));
    assert(JSON.stringify(got) === JSON.stringify(want), `A = ${award}: (P, G, R) = (${got}), expected (${want})`);
  }
});

test('award.applied_after_armies_before_fates_for_outcomes_1_to_3', () => {
  const apply = defsOf(APPLY_EFFECTS).get('ck3xbl_apply_effect').block[0].block;
  const gate = apply.find((n) => n.key === 'if' && n.block.some((c) => c.key === 'ck3xbl_apply_award_slot_effect'));
  assert(gate, 'no outcome-gated block with the award calls');
  const limit = field(gate, 'limit').block.map((c) => `${c.key} ${c.op} ${c.value}`).join(' ');
  assert(limit === 'scope:ck3xbl_res_outcome >= 1 scope:ck3xbl_res_outcome <= 3', `award gate: ${limit}`);
  // The army slots (payloads 2 and 3 only; payload 4 applies none at the click, PROTOCOL 14.4), then the awards, then the fates.
  const order = gate.block.map((n) => (n.key === 'if' && n.block.some((c) => c.key === 'ck3xbl_apply_army_slot_effect') ? 'army slots' : n.key))
    .filter((k) => k !== 'limit');
  const last = (k) => order.lastIndexOf(k);
  const first = (k) => order.indexOf(k);
  assert(first('army slots') >= 0 && last('army slots') < first('ck3xbl_apply_award_slot_effect') &&
    last('ck3xbl_apply_award_slot_effect') < first('ck3xbl_apply_char_slot_effect'), `order in the apply: ${[...new Set(order)].join(', ')}`);
  const awards = gate.block.filter((n) => n.key === 'ck3xbl_apply_award_slot_effect');
  assert(awards.length === AWARD_SLOTS, `${awards.length} award calls, expected ${AWARD_SLOTS}`);
  awards.forEach((n, k) => {
    const r = awardSlotRegisters(k);
    assert(field(n, 'TAG').value === `ck3xbl_r${r.tag}` && field(n, 'AWARD').value === `ck3xbl_r${r.award}`, `award slot ${k} reads the wrong registers`);
  });
  let outside = 0;
  walk(apply.filter((n) => n !== gate), (n) => n.key === 'ck3xbl_apply_award_slot_effect' && outside++);
  assert(outside === 0, 'an award call outside the outcome 1..3 block');
});

// --- generators -------------------------------------------------------------------------------------------------

test('gen.generators_up_to_date', () => {
  // The mod generator and every tools/gen/knights generator: the shared renderers (knight_eff_emit.mjs, accolade_emit.mjs,
  // emit_artifacts.mjs) must leave the knights outputs byte-identical.
  const knights = path.join(HERE, 'knights');
  for (const args of [
    [path.join(HERE, 'gen_ck3_mod.mjs'), '--check'],
    [path.join(knights, 'gen_knight_effectiveness.mjs'), '--check', '--quiet', '--prowess', '--emit-ck3', KE_DRAFT],
    [path.join(knights, 'gen_ck3_accolade_types.mjs'), '--check'],
    [path.join(knights, 'gen_combat_traits.mjs'), '--check'],
    [path.join(knights, 'gen_artifact_tables.mjs'), '--check'],
  ]) {
    const run = spawnSync(process.execPath, args, { encoding: 'utf8' });
    const tail = `${run.stdout}${run.stderr}`.trim().split('\n').slice(-3).join('\n');
    assert(run.status === 0, `${path.relative(REPO, args[0])} ${args.slice(1).join(' ')}: exit ${run.status}\n${tail}`);
  }
});

// --- lint mutations ---------------------------------------------------------------------------------------------

const modText = (rel) => fs.readFileSync(path.join(MOD, ...rel.split('/')), 'utf8');

/** Inserts `text` after the first line containing `anchor` in `rel`. */
function insertAfter(rel, anchor, text) {
  const src = modText(rel);
  const at = src.indexOf(anchor);
  if (at < 0) throw new Error(`anchor '${anchor}' not found in ${rel}`);
  const eol = src.indexOf('\n', at) + 1;
  return { [rel]: src.slice(0, eol) + text + '\n' + src.slice(eol) };
}

function replaceOnce(rel, from, to) {
  const src = modText(rel);
  if (!src.includes(from)) throw new Error(`'${from}' not found in ${rel}`);
  return { [rel]: src.replace(from, to) };
}

const SNAP = 'common/scripted_effects/ck3xbl_snapshot_effects.txt';
const TRIG = 'common/scripted_triggers/ck3xbl_triggers.txt';
const EVENTS = 'events/ck3xbl_battle_events.txt';
const LOC_FR = 'localization/french/ck3xbl_l_french.yml';
const STEER_HAND = 'common/scripted_effects/ck3xbl_steer_effects.txt';
const CLOCK_EVENTS = 'events/ck3xbl_clock_events.txt';

const MUTATIONS = [
  ['trigger in an effect block', insertAfter(SNAP, 'ck3xbl_snapshot_effect = {', '\tcombat_attacker = { is_ai = no }'),
    "trigger 'is_ai' used in an effect block"],
  ['effect in a scope it does not support', insertAfter(SNAP, 'ck3xbl_emit_cx_effect = {', '\tculture = { add_prestige = 5 }'),
    "'add_prestige' is not valid in culture scope"],
  ['effect in a limit', insertAfter(SNAP, 'ck3xbl_snapshot_effect = {', '\tif = { limit = { add_prestige = 5 } }'),
    "effect 'add_prestige' used in a trigger block"],
  ['link from the wrong scope', insertAfter(SNAP, 'ck3xbl_emit_cx_effect = {', '\tarmy_owner = { save_scope_as = ck3xbl_t }'),
    "link 'army_owner' needs army scope, not character"],
  ['saved scope that is never saved', insertAfter(SNAP, 'ck3xbl_snapshot_effect = {', '\tscope:ck3xbl_typo = { save_scope_as = ck3xbl_t }'),
    'scope:ck3xbl_typo is never set in the mod'],
  ['scripted trigger in an effect block', insertAfter(SNAP, 'ck3xbl_snapshot_effect = {', '\tck3xbl_combat_wanted_trigger = yes'),
    "scripted trigger 'ck3xbl_combat_wanted_trigger' used in an effect block"],
  ['portrait of a value', replaceOnce(EVENTS, 'character = scope:ck3xbl_enemy_leader', 'character = scope:ck3xbl_bid'),
    "'scope:ck3xbl_bid' is value, expected character"],
  ['target of the wrong type', replaceOnce(TRIG, 'is_at_war_with = scope:ck3xbl_player', 'is_at_war_with = scope:ck3xbl_prov'),
    "'scope:ck3xbl_prov' is province, expected character"],
  ['value trigger in the wrong scope', insertAfter(SNAP, 'ck3xbl_save_char_values_effect = {', '\tsave_scope_value_as = { name = ck3xbl_t value = army_size }'),
    "'army_size' is not valid in character scope"],
  ['trigger of the wrong scope in a limit', insertAfter(SNAP, 'ck3xbl_snapshot_effect = {', '\tif = { limit = { is_ai = no } }'),
    "'is_ai' is not valid in combat scope"],
  ['unknown modifier tag', replaceOnce(STEER_MODIFIERS, 'army_damage_mult = 0.01', 'army_damag_mult = 0.01'),
    "unknown modifier tag 'army_damag_mult'"],
  ['modifier scale without a script value', replaceOnce(STEER_MODIFIERS, 'value = ck3xbl_st_dmg_up_scale', 'value = ck3xbl_st_dmg_up_scal'),
    'no script value ck3xbl_st_dmg_up_scal'],
  ['modifier without localization', replaceOnce(STEER_LOC, ' ck3xbl_st_hard_desc:0', ' ck3xbl_st_hard_dsc:0'),
    'missing localization key ck3xbl_st_hard_desc'],
  ['key missing in French', replaceOnce(LOC_FR, ' ck3xbl_battle.0003.a:0 "Ainsi soit-il."', ''),
    'missing french localization key ck3xbl_battle.0003.a'],
  ['French text with other [...] than English', replaceOnce(LOC_FR, '[ck3xbl_enemy_leader.GetTitledFirstName] près de', '[ck3xbl_enemy_leader.GetFirstName] près de'),
    'ck3xbl_battle.0001.desc.attacking: [...] differ from english'],
  ['unknown character modifier', insertAfter(SNAP, 'ck3xbl_save_char_values_effect = {', '\tremove_character_modifier = ck3xbl_st_dmg_upp'),
    'unknown character modifier ck3xbl_st_dmg_upp'],
  ['root in a battle clock value', insertAfter(STEER_VALUES, 'ck3xbl_cday_marks_value = {', '\tif = { limit = { root = { is_ai = no } } add = 1 }'),
    'ck3xbl_cday_marks_value uses root'],
  ['combat phase event of an unknown type', replaceOnce(SHIELD, 'type = knight', 'type = squire'),
    'type must be knight or commander'],
  ['combat phase event trigger in the wrong scope', replaceOnce(SHIELD, 'is_valid = { scope:combat_side = { has_variable = ck3xbl_st_role } }', 'is_valid = { army_size > 0 }'),
    "'army_size' is not valid in character scope"],
  // knights and commanders (PROTOCOL 13.2)
  ['army trigger in character scope', insertAfter(SNAP, 'ck3xbl_emit_cx_effect = {', '\tif = { limit = { is_army_in_siege = yes } }'),
    "'is_army_in_siege' is not valid in character scope"],
  ['accolade effect in character scope', insertAfter(SNAP, 'ck3xbl_emit_cx_effect = {', '\tadd_glory = 5'),
    "'add_glory' is not valid in character scope"],
  ['artifact trigger outside an artifact', insertAfter(SNAP, 'ck3xbl_emit_cx_effect = {', '\tif = { limit = { has_artifact_feature = armor_helmet_nasal } }'),
    "'has_artifact_feature' is not valid in character scope"],
  ['trait level in army scope', insertAfter(KNIGHT_EFFECTS, 'ck3xbl_emit_ax_army_effect = {', '\tif = { limit = { has_trait_xp = { trait = the_wake value >= 100 } } }'),
    "'has_trait_xp' is not valid in army scope"],
  ['unknown parameter of a block trigger', replaceOnce(KNIGHT_EFFECTS, 'has_trait_xp = { trait = the_wake value >= 100 }', 'has_trait_xp = { trait = the_wake levle >= 100 }'),
    "unexpected 'levle' in has_trait_xp"],
  ['trait level outside its chain', replaceOnce(KNIGHT_EFFECTS, '"CK3XBL|CX|txp|forest_fighter|3"', '"CK3XBL|CX|txp|forest_fighter|4"'),
    'forest_fighter has no level 4'],
  ['territory outside the record grammar', replaceOnce(KNIGHT_EFFECTS, '"CK3XBL|AX|territory|other"', '"CK3XBL|AX|territory|neutral"'),
    "'CK3XBL|AX|territory|neutral' does not match the record grammar"],
  ['award record without its dump', replaceOnce(APPLY_EFFECTS, 'info_log = "CK3XBL|RES|AW"\n\t\tinfo_log_scopes = yes', 'info_log = "CK3XBL|RES|AW"'),
    "record 'CK3XBL|RES|AW' carries values but is not followed by info_log_scopes = yes"],
  // battle clock (PROTOCOL 14)
  ['battle clock record without its dump', replaceOnce(STEER_HAND, 'info_log = "CK3XBL|ST|WIN"\n\tinfo_log_scopes = yes', 'info_log = "CK3XBL|ST|WIN"'),
    "record 'CK3XBL|ST|WIN' carries values but is not followed by info_log_scopes = yes"],
  ['battle clock record outside the grammar', replaceOnce(STEER_HAND, '"CK3XBL|ST|SUSPEND"', '"CK3XBL|ST|PAUSE"'),
    "'CK3XBL|ST|PAUSE' does not match the record grammar"],
  ['counter changed before it is created', replaceOnce(STEER_HAND, 'set_variable = { name = ck3xbl_st_susp_days value = 0 }', 'remove_variable = ck3xbl_st_susp_days'),
    'ck3xbl_st_susp_days is changed but no effect of the mod creates it'],
  ['pulse event of an unknown scripted effect', replaceOnce(CLOCK_EVENTS, 'combat = { ck3xbl_st_pulse_tick_effect = yes }', 'combat = { ck3xbl_st_pulse_tik_effect = yes }'),
    'ck3xbl_st_pulse_tik_effect'],
  // errors of the first in-game run (error.log 2026-10-04)
  ['court type without the royal court guard', replaceOnce(KNIGHT_EFFECTS, 'has_royal_court = yes has_court_type = court_warlike', 'has_court_type = court_warlike'),
    'has_court_type needs has_royal_court = yes before it'],
  ['holder of the holy site controller', replaceOnce(KNIGHT_EFFECTS, 'holy_site:santiago = { holy_site_county = { holder ?= scope:ck3xbl_kl } }',
    'holy_site:santiago = { holy_site_controller = { holder = scope:ck3xbl_kl } }'), "link 'holder' needs landed_title scope, not character"],
];

test('lint.clean_mod', () => {
  const { problems, errors } = lintMod();
  assert(errors === 0, `the mod has lint errors:\n${problems.map((p) => `${p.file}:${p.line}: ${p.message}`).join('\n')}`);
});

for (const [name, overrides, expected] of MUTATIONS) {
  test(`lint.catches ${name}`, () => {
    const { problems } = lintMod({ overrides });
    const messages = problems.filter((p) => p.level === 'error').map((p) => p.message);
    assert(messages.some((m) => m.includes(expected)), `expected "${expected}", got:\n${messages.join('\n') || '(no errors)'}`);
  });
}

// ---------------------------------------------------------------------------------------------------------------

const filter = process.argv[2] || '';
let failed = 0;
for (const t of tests.filter((x) => x.name.includes(filter))) {
  try {
    t.fn();
    console.log(`ok    ${t.name}`);
  } catch (e) {
    failed++;
    console.log(`FAIL  ${t.name}: ${e.message}`);
  }
}
console.log(failed ? `${failed} test(s) failed` : 'all tests passed');
process.exitCode = failed ? 1 : 0;
