#!/usr/bin/env node
// lint_ck3_mod.mjs - static checks for the CK3 mod "ck3xbl" (no game launch needed).
//
// Checks:
//  1. every script, GUI and localization file is UTF-8 with BOM;
//  2. braces are balanced and strings terminated;
//  3. every `key = ...` in script is known: an effect/trigger/link from the 1.20 script docs
//     (the script_docs *.log dumps), a scripted effect/trigger/script value of the mod or vanilla, a parameter
//     of the effect it belongs to, or a structural keyword of the file type;
//  4. parameters of scripted effects/triggers (mod and vanilla) match their `$PARAM$` uses at every call site;
//  5. values that must name game data exist in the user's install (cultures, heritages, gfx, eras, terrains,
//     regions, holdings, modifiers, ethnicities, traits, MaA types, death reasons, animations, themes, flags,
//     message types and filters, events, scripted GUIs, shortcuts); has_court_type only after has_royal_court = yes;
//  6. every `info_log` record matches the record grammar of docs/PROTOCOL.md, and records that carry values are
//     immediately followed by `info_log_scopes = yes`; a CX|txp level exists in that trait's chain
//     (bridge/data/ck3_combat_traits.json);
//  7. every localization key referenced by the mod exists exactly once in English (battle_event keys need all 4 suffixes);
//     every file is localization/<language>/*_l_<language>.yml starting with l_<language>:, and every other language has
//     exactly the English keys, each with the same [...], #! and \n;
//  8. scopes and contexts (lib/scopecheck.mjs): effects only where effects run, triggers only in trigger blocks,
//     every key in a scope type it supports, links/iterators/targets of the right type, every scope:/var:/global_var:
//     name set somewhere;
//  9. character modifiers: every tag is in modifiers.log, the icon exists, a `scale` names a script value of the mod or
//     vanilla, and the modifier, its _desc and the scale desc are localized; every modifier the script adds, removes or
//     tests exists;
// 10. script values of the mod are known names; the battle clock's ck3xbl_st_* and ck3xbl_cday* values never use root
//     (root is the player in the pulse);
// 11. combat phase events: type knight or commander, only is_valid/chance/effect, root = character, scope:combat_side;
// 12. every change_variable / change_global_variable on a ck3xbl_ name has a creator somewhere in the mod (set_variable,
//     set_global_variable or ck3xbl_bump_global_counter_effect), so no counter is changed before it exists.
//
// Usage: node tools/gen/lint_ck3_mod.mjs [--mod <dir>]
// Exit code 1 when an error is found. The CK3 install is read from CK3_GAME_DIR (default: found in the Steam libraries), the
// script_docs dumps from CK3XBL_SCRIPT_DOCS, default the CK3 logs folder (tools/gen/lib/game_dirs.cjs).
// Imported, it exports lintMod({ modDir, overrides }) for tests (overrides: relative path -> file text, applied in memory).

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import {
  gameDir, listFiles, parseText, readText, scriptDocsDir, stripBom, tokenize, parse, topLevelBlockKeys, topLevelKeys, walk,
} from './lib/pdx.mjs';
import { blockParameterNames, loadScriptDocs, ScopeChecker } from './lib/scopecheck.mjs';
import { ACCOLADE_MAX_LEVEL, ACCOLADE_MAX_RANK } from './knights/accolade_emit.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..');
const DOCS = scriptDocsDir();
export const DEFAULT_MOD = path.join(REPO, 'ck3_mod', 'ck3xbl');
const GAME = gameDir();

// ---------------------------------------------------------------------------------------------------------------
// Reference data (script docs and the user's install, read-only)
// ---------------------------------------------------------------------------------------------------------------

function docNames(file) {
  const names = new Set();
  for (const m of readText(path.join(DOCS, file)).matchAll(/^([A-Za-z_][A-Za-z0-9_]*) - /gm)) names.add(m[1]);
  return names;
}

/** on_action name -> "Expected Scope" from on_actions.log. */
function onActionScopes() {
  const out = new Map();
  for (const m of readText(path.join(DOCS, 'on_actions.log')).matchAll(/^([A-Za-z0-9_]+):\s*\r?\n(?:.*\r?\n)*?Expected Scope: ([a-z_]+)/gm)) {
    out.set(m[1], m[2]);
  }
  return out;
}

function g(...parts) {
  return path.join(GAME, ...parts);
}

/** Realm law keys: blocks with a law_group_type, modifier, can_keep or can_have, at the top level or inside a law group. */
function lawKeys(dir) {
  const keys = new Set();
  const isLaw = (n) => n.block && n.block.some((c) => ['law_group_type', 'modifier', 'can_keep', 'can_have'].includes(c.key));
  for (const file of listFiles(dir, ['.txt'])) {
    for (const top of parseText(readText(file))) {
      if (!top.key || !top.block) continue;
      if (isLaw(top)) keys.add(top.key);
      for (const law of top.block) if (law.key && isLaw(law)) keys.add(law.key);
    }
  }
  return keys;
}

/** Values listed inside `<blockKey> = { a b c }` blocks anywhere in the files of `dir`. */
function listedValues(dir, blockKeys) {
  const out = new Set();
  for (const file of listFiles(dir, ['.txt'])) {
    walk(parseText(readText(file)), (node) => {
      if (node.key && blockKeys.includes(node.key)) {
        if (node.block) node.block.forEach((c) => c.key === null && c.value && out.add(c.value));
        else if (node.value) out.add(node.value);
      }
    });
  }
  return out;
}

/** `$PARAM$` names used in a definition body (parameters of a scripted effect/trigger). */
function paramsOf(node) {
  const params = new Set();
  const scan = (n) => {
    for (const c of n.block || []) {
      for (const text of [c.key, c.value]) {
        if (typeof text === 'string') for (const m of text.matchAll(/\$([A-Za-z0-9_]+)\$/g)) params.add(m[1]);
      }
      scan(c);
    }
  };
  scan(node);
  return params;
}

function scriptedDefs(dir) {
  const defs = new Map();
  for (const file of listFiles(dir, ['.txt'])) {
    for (const node of parseText(readText(file))) {
      if (node.key && node.block && !node.key.startsWith('@')) defs.set(node.key, paramsOf(node));
    }
  }
  return defs;
}

const REF = {
  effects: docNames('effects.log'),
  triggers: docNames('triggers.log'),
  links: docNames('event_targets.log'),
  onActionScopes: onActionScopes(),
  scriptDocs: loadScriptDocs(DOCS),
  vanillaEffects: scriptedDefs(g('common', 'scripted_effects')),
  vanillaTriggers: scriptedDefs(g('common', 'scripted_triggers')),
  scriptValues: topLevelKeys(g('common', 'script_values')),
  vanillaOnActions: topLevelKeys(g('common', 'on_action'), { recursive: true }),
  cultures: topLevelBlockKeys(g('common', 'culture', 'cultures')),
  pillars: topLevelBlockKeys(g('common', 'culture', 'pillars')),
  eras: topLevelBlockKeys(g('common', 'culture', 'eras')),
  terrains: topLevelBlockKeys(g('common', 'terrain_types')),
  regions: topLevelBlockKeys(g('map_data', 'geographical_regions')),
  holdings: topLevelBlockKeys(g('common', 'holdings')),
  modifiers: topLevelBlockKeys(g('common', 'modifiers')),
  laws: lawKeys(g('common', 'laws')),
  ethnicities: topLevelBlockKeys(g('common', 'ethnicities')),
  traits: topLevelBlockKeys(g('common', 'traits')),
  maa: topLevelBlockKeys(g('common', 'men_at_arms_types')),
  deathReasons: topLevelBlockKeys(g('common', 'deathreasons')),
  animations: topLevelBlockKeys(g('gfx', 'portraits', 'portrait_animations')),
  themes: topLevelBlockKeys(g('common', 'event_themes')),
  vanillaFilters: topLevelBlockKeys(g('common', 'message_filter_types')),
  vanillaMessages: topLevelBlockKeys(g('common', 'messages')),
  unitGfx: listedValues(g('common', 'culture', 'cultures'), ['unit_gfx']),
  clothingGfx: listedValues(g('common', 'culture', 'cultures'), ['clothing_gfx']),
  buildingGfx: listedValues(g('common', 'culture', 'cultures'), ['building_gfx']),
  govFlags: listedValues(g('common', 'governments'), ['flags']),
  shortcuts: new Set([...readText(g('gui', 'shortcuts.shortcuts')).matchAll(/^\s*(_[A-Za-z0-9_]+)\s*=/gm)].map((m) => m[1])),
  // Modifier tags with a fixed name (templated tags such as $FAITH$_opinion are not used by the mod).
  modifierTags: new Set([...readText(path.join(DOCS, 'modifiers.log')).matchAll(/^Tag: ([A-Za-z0-9_]+)\s*$/gm)].map((m) => m[1])),
  modifierIcons: new Set(listFiles(g('gfx', 'interface', 'icons', 'modifiers'), ['.dds']).map((f) => path.basename(f, '.dds'))),
};

// Value checks: the right-hand side of these keys must name existing game data.
const VALUE_SETS = {
  is_maa_type: REF.maa,
  has_trait: REF.traits,
  has_cultural_pillar: REF.pillars,
  has_unit_gfx: REF.unitGfx,
  has_clothing_gfx: REF.clothingGfx,
  has_building_gfx: REF.buildingGfx,
  has_cultural_era_or_later: REF.eras,
  terrain: REF.terrains,
  geographical_region: REF.regions,
  has_holding_type: REF.holdings,
  has_province_modifier: REF.modifiers,
  has_ethnicity: REF.ethnicities,
  government_has_flag: REF.govFlags,
  death_reason: REF.deathReasons,
  has_realm_law: REF.laws,
  animation: REF.animations,
  theme: REF.themes,
};

// Structural keywords valid in any script body (control flow, iterator/effect parameters, value math).
const SCRIPT_KEYWORDS = new Set([
  'if', 'else_if', 'else', 'limit', 'OR', 'AND', 'NOT', 'NOR', 'NAND', 'trigger_if', 'trigger_else_if', 'trigger_else',
  'root', 'this', 'prev', 'from',
  'value', 'add', 'subtract', 'multiply', 'divide', 'modulo', 'min', 'max', 'round', 'floor', 'ceiling',
  'name', 'target', 'variable', 'list', 'type', 'days', 'months', 'years', 'id', 'on_action',
  'title', 'desc', 'tooltip', 'left_icon', 'right_icon', 'key', 'left_portrait', 'right_portrait', 'target_right',
  'reason', 'killer', 'text', 'subject', 'object', 'order_by', 'position', 'check_range_bounds', 'include_hired',
  'count', 'percent', 'weight', 'base', 'modifier', 'factor', 'levies', 'men_at_arms', 'stacks', 'men',
]);

const EVENT_FIELDS = new Set([
  'type', 'title', 'desc', 'theme', 'hidden', 'trigger', 'immediate', 'after', 'option', 'widget', 'widgets',
  'left_portrait', 'right_portrait', 'center_portrait', 'lower_left_portrait', 'lower_center_portrait', 'lower_right_portrait',
  'override_background', 'cooldown', 'scope',
]);
const DESC_KEYS = new Set(['desc', 'first_valid', 'random_valid', 'triggered_desc', 'trigger']);
const PORTRAIT_KEYS = new Set(['character', 'animation', 'triggered_animation', 'trigger', 'camera', 'outfit_tags', 'hide_info']);
const WIDGET_KEYS = new Set(['gui', 'container', 'is_shown', 'controller', 'setup_scope']);
const OPTION_KEYS = new Set(['name', 'trigger', 'show_as_unavailable', 'highlight_portrait', 'ai_chance', 'fallback', 'exclusive']);
const ON_ACTION_KEYS = new Set(['trigger', 'effect', 'events', 'random_events', 'on_actions', 'random_on_actions', 'first_valid', 'first_valid_on_action', 'weight_multiplier']);
const SCRIPTED_GUI_KEYS = new Set(['scope', 'saved_scopes', 'is_shown', 'is_valid', 'effect', 'ai_is_valid', 'ai_chance', 'ai_frequency']);
const MESSAGE_KEYS = new Set(['display', 'message_filter_type', 'text', 'title', 'desc', 'tooltip', 'soundeffect', 'icon', 'style', 'flag']);
const FILTER_KEYS = new Set(['display', 'always_show', 'auto_pause', 'sort_order', 'group']);
const GUI_KEYS = new Set(['vbox', 'hbox', 'widget', 'button', 'text_single', 'text_multi', 'name', 'size', 'position', 'shortcut',
  'onclick', 'text', 'align', 'layoutpolicy_horizontal', 'layoutpolicy_vertical', 'margin', 'autoresize', 'visible', 'tooltip']);
// Character modifiers (game/common/modifiers/_modifiers.info) and the script keys whose value names one.
const SCALE_KEYS = new Set(['value', 'desc', 'display_mode']);
const SCALE_DISPLAY_MODES = new Set(['scaled']);
const MODIFIER_REF_KEYS = new Set(['add_character_modifier', 'remove_character_modifier', 'remove_all_character_modifier_instances', 'has_character_modifier']);
// Combat phase events (game/common/combat_phase_events/_combat_phase_events.info).
const PHASE_EVENT_KEYS = new Set(['type', 'is_valid', 'chance', 'effect']);
const PHASE_EVENT_TYPES = new Set(['knight', 'commander']);
// Script values of the battle clock work on `this`; their root is the player when the pulse evaluates them.
const STEERING_VALUE = /^ck3xbl_(?:st_|cday)/;
const ROOT_REF = /(?:^|\.)root(?:\.|$)/;

// Root scopes of the vanilla on_actions the mod hooks (vanilla comments in common/on_action; on_actions.log says
// "none" for some code on_actions), and the saved scopes the engine provides there.
const ON_ACTION_ROOTS = {
  on_combat_start: 'combat',
  on_combat_unit_join_side: 'combat_side',
  on_combat_end_winner: 'combat_side',
  on_combat_end_loser: 'combat_side',
  on_army_enter_province: 'character',
  on_player_character_change: 'character',
};
const ON_ACTION_SAVED_SCOPES = { army: 'army', wipe: 'boolean' };
const PHASE_EVENT_SAVED_SCOPES = { combat_side: 'combat_side' };
const EVENT_ROOTS = { character_event: 'character' };

const PREFIXED = /^(scope|var|global_var|local_var|culture|faith|title|character|province|flag|religion|dynasty|house|holy_site|confederation_type|accolade_type):/;

// Leveled traits (CX|txp): "<trait>[:<track>]" -> the levels of its chain in bridge/data/ck3_combat_traits.json. The grammar's
// highest level is derived from the data, and every emitted level must exist in that trait's own chain.
const TXP_LEVELS = txpLevels();
const TXP_MAX_LEVEL = Math.max(...[...TXP_LEVELS.values()].flatMap((levels) => [...levels]));
if (TXP_MAX_LEVEL > 9) throw new Error(`ck3_combat_traits.json: trait level ${TXP_MAX_LEVEL} needs a wider CX|txp grammar`);

function txpLevels() {
  const combat = JSON.parse(stripBom(readText(path.join(REPO, 'bridge', 'data', 'ck3_combat_traits.json'))));
  const levels = new Map();
  for (const trait of Object.values(combat.traits)) {
    for (const track of trait.emit?.levels || []) {
      const key = /^CX\|txp\|([^|]+)\|<level>$/.exec(track.record)?.[1];
      if (key) levels.set(key, new Set(track.chain.map((c) => c.level)));
    }
  }
  return levels;
}

const RECORD = new RegExp('^CK3XBL\\|(?:' + [
  'B\\|(?:BEGIN|END)', 'L\\|[a-z_]+\\|[A-Za-z0-9_]+', 'LV', 'S\\|(?:att|def)', 'A\\|(?:att|def)', 'R',
  'C\\|(?:CMD|KNIGHT|PRIMARY|PLAYER)\\|(?:att|def)',
  'CX\\|(?:culture|heritage|unit_gfx|clothing_gfx|ethnicity|era|trait|ctrait)\\|[A-Za-z0-9_]+',
  'AX\\|(?:culture|heritage|unit_gfx|era|nomadic)\\|[A-Za-z0-9_]+', 'RX\\|(?:culture|heritage|unit_gfx)\\|[A-Za-z0-9_]+',
  // Knights and commanders (docs/PROTOCOL.md 13.2); artifact knight effectiveness (AX|ke_art) is not logged.
  'CX\\|(?:ktrait|cparam|fparam)\\|[A-Za-z0-9_]+', `CX\\|txp\\|[A-Za-z0-9_]+(?::[A-Za-z0-9_]+)?\\|[1-${TXP_MAX_LEVEL}]`,
  'CX\\|acc_active\\|(?:yes|no)', `CX\\|acc_rank\\|[0-${ACCOLADE_MAX_RANK}]`, 'CX\\|acc_primary\\|[A-Za-z0-9_]+',
  `CX\\|acc_type\\|[A-Za-z0-9_]+\\|[0-${ACCOLADE_MAX_LEVEL}]`,
  // equipped artifacts (tools/gen/knights/emit_artifacts.mjs, not imported: its --lint imports this file)
  'CX\\|artifact\\|(?:primary_armament|armor|helmet)\\|[A-Za-z0-9_]+\\|(?:common|masterwork|famed|illustrious)', 'CX\\|artifact_helmet\\|[A-Za-z0-9_]+',
  'AX\\|besieging\\|(?:yes|no)', 'AX\\|territory\\|(?:own|hostile|other)', 'AX\\|(?:otrait|ocparam|ofparam)\\|[A-Za-z0-9_]+',
  'CHOICE\\|(?:FIELD|AUTO|ABANDON)', 'J\\|(?:att|def)', 'IN\\|(?:OK|BAD)', 'RES\\|(?:BEGIN|LIVE|GONE|A|AW|END)', 'END\\|(?:winner|loser)',
  // battle clock (docs/PROTOCOL.md 14.5)
  'ST\\|(?:BEGIN|P|SUSPEND|RESUME|WIN|HANDBACK|FIX|DONE)',
].join('|') + ')$');
const DUMPLESS = /^CK3XBL\|(?:L|CX|AX|RX)\|/;

/**
 * True when an earlier statement of the same trigger block, or of an enclosing one (or an earlier limit there), is
 * `has_royal_court = yes`. has_court_type errors for a character without a royal court ("Character must have a royal court";
 * in game, 2026-10-04), so vanilla tests the court first (common/scripted_triggers/00_artifact_triggers.txt:2210-2214).
 */
function guardedByRoyalCourt(node, parents) {
  const isGuard = (n) => n.key === 'has_royal_court' && n.value === 'yes';
  let cur = node;
  for (let level = parents.length - 1; level >= 0; level--) {
    const siblings = parents[level].block || [];
    const before = siblings.slice(0, siblings.indexOf(cur));
    if (before.some((n) => isGuard(n) || (n.key === 'limit' && (n.block || []).some(isGuard)))) return true;
    cur = parents[level];
  }
  return false;
}

/**
 * A record with values must be followed by `info_log_scopes = yes`. Looks at the statement after `node`; when
 * `node` is the last statement of an if/else_if/else body, continues after the whole if/else chain.
 */
function followedByDump(node, parents) {
  let cur = node;
  for (let level = parents.length - 1; level >= 0; level--) {
    const siblings = parents[level].block;
    let i = siblings.indexOf(cur) + 1;
    if (cur !== node && cur.key !== 'else') {
      while (i < siblings.length && (siblings[i].key === 'else_if' || siblings[i].key === 'else')) i++;
    }
    if (i < siblings.length) return siblings[i].key === 'info_log_scopes' && siblings[i].value === 'yes';
    if (!['if', 'else_if', 'else'].includes(parents[level].key)) return false;
    cur = parents[level];
  }
  return false;
}

// ---------------------------------------------------------------------------------------------------------------
// Lint of one mod folder
// ---------------------------------------------------------------------------------------------------------------

/**
 * Lints the mod in `modDir`. `overrides` maps relative paths (forward slashes) to file text that replaces the file
 * on disk (or adds it); the BOM is added unless the text starts with one. Returns { problems, summary }.
 */
export function lintMod({ modDir = DEFAULT_MOD, overrides = {} } = {}) {
  const MOD = path.resolve(modDir);
  const rel = (f) => path.relative(MOD, f).replace(/\\/g, '/');
  const problems = [];
  const report = (level, file, line, message) => problems.push({ level, file: rel(file), line, message });
  const error = (file, line, message) => report('error', file, line, message);

  const onDisk = listFiles(MOD, ['.txt', '.gui', '.yml'], true).filter((f) => !f.endsWith('descriptor.mod'));
  const files = [...new Set([...onDisk, ...Object.keys(overrides).map((r) => path.join(MOD, ...r.split('/')))])].sort();
  const raw = (file) => {
    const o = overrides[rel(file)];
    if (o === undefined) return fs.readFileSync(file);
    return Buffer.from(o.charCodeAt(0) === 0xfeff ? o : `﻿${o}`, 'utf8');
  };

  const parsed = new Map();
  const texts = new Map();
  for (const file of files) {
    const bytes = raw(file);
    if (!(bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf)) error(file, 1, 'missing UTF-8 BOM');
    texts.set(file, bytes.toString('utf8'));
    if (file.endsWith('.yml')) continue;
    const errs = [];
    const nodes = parse(tokenize(texts.get(file), errs), errs);
    for (const e of errs) error(file, e.line, e.message);
    parsed.set(file, nodes);
  }

  const byDir = (prefix) => [...parsed.entries()].filter(([f]) => rel(f).startsWith(prefix));

  const MODDEF = {
    effects: new Map(),
    triggers: new Map(),
    effectNodes: new Map(),
    triggerNodes: new Map(),
    guis: new Set(),
    events: new Set(),
    namespaces: new Set(),
    messages: new Set(),
    filters: new Set(),
    onActions: new Set(),
    modifiers: new Set(),
    scriptValues: new Set(),
  };
  for (const [file, nodes] of byDir('common/scripted_effects/')) {
    for (const n of nodes) {
      if (!n.key || !n.block) continue;
      MODDEF.effects.set(n.key, paramsOf(n));
      MODDEF.effectNodes.set(n.key, { node: n, file });
    }
  }
  for (const [file, nodes] of byDir('common/scripted_triggers/')) {
    for (const n of nodes) {
      if (!n.key || !n.block) continue;
      MODDEF.triggers.set(n.key, paramsOf(n));
      MODDEF.triggerNodes.set(n.key, { node: n, file });
    }
  }
  for (const [, nodes] of byDir('common/scripted_guis/')) for (const n of nodes) if (n.key) MODDEF.guis.add(n.key);
  for (const [, nodes] of byDir('common/messages/')) for (const n of nodes) if (n.key) MODDEF.messages.add(n.key);
  for (const [, nodes] of byDir('common/message_filter_types/')) for (const n of nodes) if (n.key) MODDEF.filters.add(n.key);
  for (const [, nodes] of byDir('common/on_action/')) for (const n of nodes) if (n.key) MODDEF.onActions.add(n.key);
  for (const [, nodes] of byDir('common/modifiers/')) for (const n of nodes) if (n.key) MODDEF.modifiers.add(n.key);
  for (const [, nodes] of byDir('common/script_values/')) for (const n of nodes) if (n.key) MODDEF.scriptValues.add(n.key);
  for (const [, nodes] of byDir('events/')) {
    for (const n of nodes) {
      if (n.key === 'namespace') MODDEF.namespaces.add(n.value);
      else if (n.key) MODDEF.events.add(n.key);
    }
  }

  const allEffects = new Map([...REF.vanillaEffects, ...MODDEF.effects]);
  const allTriggers = new Map([...REF.vanillaTriggers, ...MODDEF.triggers]);
  const allScriptValues = new Set([...REF.scriptValues, ...MODDEF.scriptValues]);
  const allModifiers = new Set([...REF.modifiers, ...MODDEF.modifiers]);

  // The scope pass resolves the mod's script values as values, like vanilla ones.
  const scopes = new ScopeChecker({
    docs: REF.scriptDocs,
    defs: { effects: MODDEF.effectNodes, triggers: MODDEF.triggerNodes },
    vanilla: { effects: new Set(REF.vanillaEffects.keys()), triggers: new Set(REF.vanillaTriggers.keys()), scriptValues: allScriptValues },
    onError: error,
  });
  for (const [name, type] of Object.entries({ ...ON_ACTION_SAVED_SCOPES, ...PHASE_EVENT_SAVED_SCOPES })) scopes.seedSaved(name, type);

  // --- script checks (names, parameters, game data, records, localization references) ---------------------

  // Check 12: names an effect of the mod creates (set_variable, set_global_variable, the global counter effect).
  const created = new Set();
  const changed = []; // {name, file, line}
  for (const [file, nodes] of parsed) {
    walk(nodes, (n) => {
      if (['set_variable', 'set_global_variable', 'change_variable', 'change_global_variable'].includes(n.key) && n.block) {
        const name = n.block.find((c) => c.key === 'name')?.value;
        if (!name || name.includes('$')) return;
        if (n.key.startsWith('set_')) created.add(name);
        else if (name.startsWith('ck3xbl_')) changed.push({ name, file, line: n.line });
      }
      if (n.key === 'ck3xbl_bump_global_counter_effect' && n.block) {
        const name = n.block.find((c) => c.key === 'NAME')?.value;
        if (name) created.add(name);
      }
    });
  }
  for (const c of changed) if (!created.has(c.name)) error(c.file, c.line, `${c.name} is changed but no effect of the mod creates it (set it first)`);

  const locRefs = []; // {key, file, line}
  const needLoc = (key, file, line) => locRefs.push({ key, file, line });

  function checkChainSegment(seg, file, line) {
    const m = PREFIXED.exec(seg);
    if (m) {
      if (m[1] === 'culture' && !REF.cultures.has(seg.slice(8))) error(file, line, `unknown culture '${seg}'`);
      return;
    }
    if (/^\$[A-Za-z0-9_]+\$$/.test(seg) || /^-?\d+(\.\d+)?$/.test(seg)) return;
    if (['root', 'this', 'prev', 'from'].includes(seg)) return;
    if (REF.links.has(seg) || REF.triggers.has(seg) || allScriptValues.has(seg)) return;
    error(file, line, `unknown link '${seg}'`);
  }

  function checkValueToken(value, file, line) {
    if (typeof value !== 'string') return;
    for (const m of value.matchAll(/culture:([A-Za-z0-9_]+)/g)) {
      if (!REF.cultures.has(m[1])) error(file, line, `unknown culture 'culture:${m[1]}'`);
    }
  }

  function checkCall(node, defs, file) {
    const params = defs.get(node.key);
    if (!params) return false;
    if (node.block) {
      const given = new Set(node.block.filter((c) => c.key).map((c) => c.key));
      for (const p of params) if (!given.has(p)) error(file, node.line, `${node.key}: missing parameter ${p}`);
      for (const p of given) if (!params.has(p)) error(file, node.line, `${node.key}: unexpected parameter ${p}`);
    } else if (params.size) {
      error(file, node.line, `${node.key} needs parameters ${[...params].join(', ')}`);
    }
    return true;
  }

  /** Checks a script body (effects or triggers). `parents` = ancestor nodes for context. */
  function checkScript(nodes, file, parents) {
    for (const node of nodes) {
      const here = [...parents, node];
      if (node.key === null) {
        if (node.block) checkScript(node.block, file, here);
        continue;
      }
      const key = node.key;
      checkValueToken(node.value, file, node.line);
      if (node.value && VALUE_SETS[key] && !/^(scope|var|global_var):/.test(node.value) && !VALUE_SETS[key].has(node.value)) {
        error(file, node.line, `${key} = ${node.value}: no such key in the game data`);
      }
      if (key === 'info_log') {
        const txp = /^CK3XBL\|CX\|txp\|([^|]+)\|(\d+)$/.exec(node.value);
        if (!RECORD.test(node.value)) error(file, node.line, `info_log '${node.value}' does not match the record grammar`);
        else if (!DUMPLESS.test(node.value) && !followedByDump(node, parents)) {
          error(file, node.line, `record '${node.value}' carries values but is not followed by info_log_scopes = yes`);
        } else if (txp && !TXP_LEVELS.get(txp[1])?.has(Number(txp[2]))) {
          error(file, node.line, `info_log '${node.value}': ${txp[1]} has no level ${txp[2]} (bridge/data/ck3_combat_traits.json)`);
        }
      }
      if (key === 'has_court_type' && !guardedByRoyalCourt(node, parents)) {
        error(file, node.line, 'has_court_type needs has_royal_court = yes before it (an error for characters without a royal court)');
      }
      if (key === 'trigger_event' && node.value && !MODDEF.events.has(node.value)) error(file, node.line, `unknown event ${node.value}`);
      if (key === 'message_filter_type' && !MODDEF.filters.has(node.value) && !REF.vanillaFilters.has(node.value)) {
        error(file, node.line, `unknown message filter ${node.value}`);
      }
      const parent = parents[parents.length - 1];
      if (parent && parent.key === 'send_interface_toast') {
        if (key === 'type' && !MODDEF.messages.has(node.value) && !REF.vanillaMessages.has(node.value)) {
          error(file, node.line, `unknown message type ${node.value}`);
        }
        if (['title', 'desc', 'tooltip'].includes(key)) needLoc(node.value, file, node.line);
      }
      if (parent && parent.key === 'imprison' && key === 'type' && !REF.modifiers.has(node.value)) {
        error(file, node.line, `unknown prison type ${node.value}`);
      }
      const namesModifier = (MODIFIER_REF_KEYS.has(key) && node.value) || (parent && parent.key === 'add_character_modifier' && key === 'modifier');
      if (namesModifier && !allModifiers.has(node.value)) error(file, node.line, `unknown character modifier ${node.value}`);
      if (parent && ['custom_tooltip', 'custom_description'].includes(parent.key) && key === 'text') needLoc(node.value, file, node.line);
      if (parent && parent.key === 'battle_event' && key === 'key') {
        for (const suffix of ['_friendly', '_enemy', '_friendly_player', '_enemy_player']) needLoc(node.value + suffix, file, node.line);
      }

      if (checkCall(node, allEffects, file) || checkCall(node, allTriggers, file)) continue; // parameter blocks
      // A parameter of a block effect/trigger (has_trait_xp = { trait track value }, ...) is checked by the scope pass.
      const isParameter = Boolean(parent && blockParameterNames(parent.key)?.has(key));
      if (!isParameter && (key.includes('.') || PREFIXED.test(key))) {
        for (const seg of key.split('.')) checkChainSegment(seg, file, node.line);
      } else if (!isParameter && !(SCRIPT_KEYWORDS.has(key) || REF.effects.has(key) || REF.triggers.has(key) || REF.links.has(key) || allScriptValues.has(key))) {
        error(file, node.line, `unknown effect/trigger/keyword '${key}'`);
      }
      if (node.block) {
        if (key === 'switch') continue; // case keys are values
        checkScript(node.block, file, here);
      }
    }
  }

  function checkDynamicDesc(node, file, parents, root) {
    if (node.value !== undefined) {
      needLoc(node.value, file, node.line);
      return;
    }
    for (const c of node.block || []) {
      if (!c.key) continue;
      if (!DESC_KEYS.has(c.key)) error(file, c.line, `unexpected '${c.key}' in a dynamic description`);
      if (c.key === 'trigger') {
        checkScript(c.block || [], file, [...parents, node, c]);
        scopes.addEntry(c.block || [], 'trigger', root, file);
      } else checkDynamicDesc(c, file, [...parents, node], root);
    }
  }

  function checkEvent(ev, file) {
    const root = EVENT_ROOTS[(ev.block.find((f) => f.key === 'type') || {}).value] || null;
    for (const f of ev.block) {
      if (!f.key) continue;
      if (!EVENT_FIELDS.has(f.key)) error(file, f.line, `unknown event field '${f.key}'`);
      if (f.key === 'theme' && !REF.themes.has(f.value)) error(file, f.line, `unknown theme ${f.value}`);
      if (f.key === 'title' || f.key === 'desc') checkDynamicDesc(f, file, [ev], root);
      if (['immediate', 'after', 'trigger'].includes(f.key)) {
        checkScript(f.block || [], file, [ev, f]);
        scopes.addEntry(f.block || [], f.key === 'trigger' ? 'trigger' : 'effect', root, file);
      }
      if (f.key.endsWith('_portrait') && f.block) {
        for (const p of f.block) {
          if (!p.key) continue;
          if (!PORTRAIT_KEYS.has(p.key)) error(file, p.line, `unexpected '${p.key}' in a portrait`);
          if (p.key === 'character') scopes.addTarget(p.value, 'character', root, file, p.line);
          if (p.key === 'animation' && !REF.animations.has(p.value)) error(file, p.line, `unknown animation ${p.value}`);
          if (p.key === 'trigger') {
            checkScript(p.block || [], file, [ev, f, p]);
            scopes.addEntry(p.block || [], 'trigger', root, file);
          }
          if (p.key === 'triggered_animation') {
            for (const t of p.block || []) {
              if (t.key === 'trigger') {
                checkScript(t.block || [], file, [ev, f, p, t]);
                scopes.addEntry(t.block || [], 'trigger', root, file);
              } else if (t.key === 'animation') {
                if (!REF.animations.has(t.value)) error(file, t.line, `unknown animation ${t.value}`);
              } else if (t.key) error(file, t.line, `unexpected '${t.key}' in triggered_animation`);
            }
          }
        }
      }
      if (f.key === 'widget') {
        for (const w of f.block || []) {
          if (w.key && !WIDGET_KEYS.has(w.key)) error(file, w.line, `unexpected '${w.key}' in widget`);
          if (w.key === 'gui' && !files.includes(path.join(MOD, 'gui', 'event_window_widgets', `${w.value}.gui`))) {
            error(file, w.line, `widget gui '${w.value}' not found in gui/event_window_widgets`);
          }
        }
      }
      if (f.key === 'option') {
        const effects = [];
        for (const o of f.block || []) {
          if (o.key === 'name') checkDynamicDesc(o, file, [ev, f], root);
          else if (o.key === 'trigger' || o.key === 'show_as_unavailable') {
            checkScript(o.block || [], file, [ev, f, o]);
            scopes.addEntry(o.block || [], 'trigger', root, file);
          } else if (OPTION_KEYS.has(o.key)) continue;
          else effects.push(o);
        }
        checkScript(effects, file, [ev, f]);
        scopes.addEntry(effects, 'effect', root, file);
      }
    }
  }

  /** A number, or the name of a script value of the mod or vanilla. */
  const isValueName = (v) => typeof v === 'string' && (/^-?\d+(\.\d+)?$/.test(v) || allScriptValues.has(v));

  function checkModifier(def, file) {
    if (!def.block) {
      error(file, def.line, `modifier ${def.key} needs a block`);
      return;
    }
    needLoc(def.key, file, def.line);
    needLoc(`${def.key}_desc`, file, def.line);
    for (const f of def.block) {
      if (!f.key) continue;
      if (f.key === 'icon') {
        if (!REF.modifierIcons.has(f.value)) error(file, f.line, `unknown modifier icon ${f.value} (gfx/interface/icons/modifiers)`);
      } else if (f.key === 'scale') {
        for (const c of f.block || []) {
          if (!SCALE_KEYS.has(c.key)) error(file, c.line, `unexpected '${c.key}' in the scale of modifier ${def.key}`);
          else if (c.key === 'desc') needLoc(c.value, file, c.line);
          else if (c.key === 'display_mode' && !SCALE_DISPLAY_MODES.has(c.value)) error(file, c.line, `unknown display_mode ${c.value}`);
          else if (c.key === 'value' && c.block) {
            checkScript(c.block, file, [def, f, c]);
            scopes.addEntry(c.block, 'value', 'character', file);
          } else if (c.key === 'value' && !isValueName(c.value)) error(file, c.line, `scale of ${def.key}: no script value ${c.value}`);
        }
      } else if (!REF.modifierTags.has(f.key)) {
        error(file, f.line, `unknown modifier tag '${f.key}' in ${def.key} (modifiers.log)`);
      } else if (!isValueName(f.value)) {
        error(file, f.line, `${f.key} in ${def.key} needs a number or a script value`);
      }
    }
  }

  function checkScriptValue(def, file) {
    if (!def.block) {
      if (!isValueName(def.value)) error(file, def.line, `script value ${def.key} must be a number or a block`);
      return;
    }
    checkScript(def.block, file, [def]);
    scopes.addEntry(def.block, 'value', null, file);
    if (STEERING_VALUE.test(def.key)) {
      walk(def.block, (n) => {
        if ([n.key, n.value].some((t) => typeof t === 'string' && ROOT_REF.test(t))) {
          error(file, n.line, `${def.key} uses root: battle clock values work on this (root is the player in the pulse)`);
        }
      });
    }
  }

  function checkPhaseEvent(def, file) {
    const fields = (def.block || []).filter((f) => f.key);
    const type = fields.find((f) => f.key === 'type');
    if (!type || !PHASE_EVENT_TYPES.has(type.value)) error(file, def.line, `combat phase event ${def.key}: type must be knight or commander`);
    for (const f of fields) {
      if (!PHASE_EVENT_KEYS.has(f.key)) error(file, f.line, `unexpected '${f.key}' in combat phase event ${def.key}`);
      else if (f.key === 'is_valid' || f.key === 'effect') {
        checkScript(f.block || [], file, [def, f]);
        scopes.addEntry(f.block || [], f.key === 'effect' ? 'effect' : 'trigger', 'character', file);
      } else if (f.key === 'chance') {
        for (const c of (f.block || []).filter((x) => x.key)) {
          if (c.key === 'base') {
            if (!isValueName(c.value)) error(file, c.line, `chance base of ${def.key} needs a number`);
          } else if (c.key === 'modifier') {
            const triggers = (c.block || []).filter((x) => x.key !== 'factor' && x.key !== 'add');
            checkScript(triggers, file, [def, f, c]);
            scopes.addEntry(triggers, 'trigger', 'character', file);
          } else error(file, c.line, `unexpected '${c.key}' in the chance of ${def.key}`);
        }
      }
    }
  }

  // Root scope of each of the mod's on_actions: a vanilla hook's own root, or the root of the hooks listing it.
  const onActionRoot = new Map();
  for (const [, nodes] of byDir('common/on_action/')) {
    for (const def of nodes) {
      const root = ON_ACTION_ROOTS[def.key] || (REF.onActionScopes.get(def.key) !== 'none' ? REF.onActionScopes.get(def.key) : null) || null;
      if (root) onActionRoot.set(def.key, root);
      for (const f of def.block || []) {
        if (f.key === 'on_actions') for (const v of f.block || []) if (root && v.value) onActionRoot.set(v.value, root);
      }
    }
  }

  for (const [file, nodes] of parsed) {
    const r = rel(file);
    if (r.startsWith('gui/')) continue;
    if (r.startsWith('common/scripted_effects/') || r.startsWith('common/scripted_triggers/')) {
      for (const def of nodes) if (def.block) checkScript(def.block, file, [def]);
    } else if (r.startsWith('common/on_action/')) {
      for (const def of nodes) {
        const isHook = REF.onActionScopes.has(def.key) || REF.vanillaOnActions.has(def.key) || MODDEF.onActions.has(def.key);
        if (!isHook) error(file, def.line, `unknown on_action ${def.key}`);
        const root = onActionRoot.get(def.key) || null;
        for (const f of def.block || []) {
          if (!ON_ACTION_KEYS.has(f.key)) error(file, f.line, `unexpected '${f.key}' in on_action ${def.key}`);
          if (f.key === 'effect' || f.key === 'trigger') {
            checkScript(f.block || [], file, [def, f]);
            scopes.addEntry(f.block || [], f.key, root, file);
          }
          if (f.key === 'on_actions') {
            for (const v of f.block || []) if (!MODDEF.onActions.has(v.value) && !REF.vanillaOnActions.has(v.value)) error(file, v.line, `unknown on_action ${v.value}`);
          }
          if (f.key === 'events') for (const v of f.block || []) if (!MODDEF.events.has(v.value)) error(file, v.line, `unknown event ${v.value}`);
        }
      }
    } else if (r.startsWith('events/')) {
      for (const def of nodes) {
        if (def.key === 'namespace') continue;
        const ns = def.key.split('.')[0];
        if (!MODDEF.namespaces.has(ns)) error(file, def.line, `event ${def.key}: namespace ${ns} not declared`);
        checkEvent(def, file);
      }
    } else if (r.startsWith('common/scripted_guis/')) {
      for (const def of nodes) {
        const root = (def.block || []).find((f) => f.key === 'scope')?.value || null;
        for (const f of def.block || []) {
          if (!SCRIPTED_GUI_KEYS.has(f.key)) error(file, f.line, `unexpected '${f.key}' in scripted GUI ${def.key}`);
          if (['effect', 'is_shown', 'is_valid'].includes(f.key)) {
            checkScript(f.block || [], file, [def, f]);
            scopes.addEntry(f.block || [], f.key === 'effect' ? 'effect' : 'trigger', root, file);
          }
        }
      }
    } else if (r.startsWith('common/messages/')) {
      for (const def of nodes) {
        for (const f of def.block || []) {
          if (!MESSAGE_KEYS.has(f.key)) error(file, f.line, `unexpected '${f.key}' in message ${def.key}`);
          if (['text', 'title', 'desc', 'tooltip'].includes(f.key)) needLoc(f.value, file, f.line);
          if (f.key === 'message_filter_type' && !MODDEF.filters.has(f.value) && !REF.vanillaFilters.has(f.value)) error(file, f.line, `unknown message filter ${f.value}`);
        }
      }
    } else if (r.startsWith('common/message_filter_types/')) {
      for (const def of nodes) {
        needLoc(`message_filter_${def.key}`, file, def.line);
        for (const f of def.block || []) if (!FILTER_KEYS.has(f.key)) error(file, f.line, `unexpected '${f.key}' in message filter ${def.key}`);
      }
    } else if (r.startsWith('common/modifiers/')) {
      for (const def of nodes) checkModifier(def, file);
    } else if (r.startsWith('common/script_values/')) {
      for (const def of nodes) checkScriptValue(def, file);
    } else if (r.startsWith('common/combat_phase_events/')) {
      for (const def of nodes) checkPhaseEvent(def, file);
    } else {
      error(file, 1, 'file in an unexpected folder (not checked)');
    }
  }

  scopes.run();

  // GUI files
  for (const [file, nodes] of byDir('gui/')) {
    walk(nodes, (node) => {
      if (!node.key) return;
      if (!GUI_KEYS.has(node.key)) error(file, node.line, `unknown GUI property '${node.key}'`);
      if (node.key === 'shortcut' && !REF.shortcuts.has(node.value)) error(file, node.line, `shortcut ${node.value} is not defined in vanilla gui/shortcuts.shortcuts`);
      if (node.key === 'onclick') {
        for (const m of node.value.matchAll(/GetScriptedGui\('([^']+)'\)/g)) if (!MODDEF.guis.has(m[1])) error(file, node.line, `unknown scripted GUI ${m[1]}`);
      }
      if (node.key === 'text' && /^[A-Za-z0-9_.]+$/.test(node.value)) needLoc(node.value, file, node.line);
    });
  }

  // --- localization ----------------------------------------------------------------------------------------

  // One key table per language (localization/<language>/*_l_<language>.yml). English is the reference: the mod's references
  // are checked against it, and every other language must have exactly its keys, with the same [...], #! and \n per key.
  const REF_LANG = 'english';
  const locByLang = new Map();
  const firstFileOf = new Map();
  for (const file of files.filter((f) => f.endsWith('.yml'))) {
    const lines = stripBom(texts.get(file)).split(/\r?\n/);
    const header = lines.findIndex((l) => l.trim() && !l.trim().startsWith('#'));
    const dir = /^localization\/([a-z_]+)\//.exec(rel(file));
    const lang = dir ? dir[1] : null;
    if (!lang) error(file, 1, 'localization file must be in localization/<language>/');
    else {
      if (header < 0 || lines[header].trim() !== `l_${lang}:`) error(file, header + 1, `first entry must be l_${lang}:`);
      if (!path.basename(file).endsWith(`_l_${lang}.yml`)) error(file, 1, `file name must end with _l_${lang}.yml`);
    }
    const table = locByLang.get(lang) ?? new Map();
    locByLang.set(lang, table);
    if (!firstFileOf.has(lang)) firstFileOf.set(lang, file);
    lines.forEach((l, i) => {
      if (i <= header || !l.trim() || l.trim().startsWith('#')) return;
      const m = /^ ([A-Za-z0-9_.\-]+):\d* "(.*)"\s*$/.exec(l);
      if (!m) { error(file, i + 1, 'malformed localization line'); return; }
      if (table.has(m[1])) error(file, i + 1, `duplicate localization key ${m[1]}`);
      table.set(m[1], { file, line: i + 1, text: m[2] });
      const text = m[2];
      if ((text.match(/\[/g) || []).length !== (text.match(/\]/g) || []).length) error(file, i + 1, `unbalanced [ ] in ${m[1]}`);
      const opens = (text.match(/#[A-Za-z]+[ ]/g) || []).length;
      const closes = (text.match(/#!/g) || []).length;
      if (opens !== closes) error(file, i + 1, `unbalanced #format ... #! in ${m[1]}`);
      if (/(?<!\\)"/.test(text)) error(file, i + 1, `unescaped quote in ${m[1]}`);
      if (lang !== REF_LANG) return;   // the reference language alone declares what the localization refers to
      // the last two arguments of SelectLocalization( condition, 'key_if_true', 'key_if_false' )
      for (const r of text.matchAll(/SelectLocalization\(.*?,\s*'([A-Za-z0-9_.]+)'\s*,\s*'([A-Za-z0-9_.]+)'\s*\)/g)) {
        needLoc(r[1], file, i + 1);
        needLoc(r[2], file, i + 1);
      }
      for (const r of text.matchAll(/\$([A-Za-z0-9_.]+)\$/g)) needLoc(r[1], file, i + 1);
    });
  }
  const loc = locByLang.get(REF_LANG) ?? new Map();
  // Same keys as English in every language, and per key the same [...] (as a multiset: a translation may reorder them),
  // the same number of #! and of \n.
  const brackets = (t) => (t.match(/\[[^\]]*\]/g) || []).sort().join(' ');
  const count = (t, rx) => (t.match(rx) || []).length;
  for (const [lang, table] of locByLang) {
    if (lang === REF_LANG || lang === null) continue;
    for (const [key, ref] of loc) {
      const own = table.get(key);
      if (!own) { error(firstFileOf.get(lang), 1, `missing ${lang} localization key ${key}`); continue; }
      if (brackets(own.text) !== brackets(ref.text)) error(own.file, own.line, `${key}: [...] differ from ${REF_LANG}`);
      if (count(own.text, /#!/g) !== count(ref.text, /#!/g)) error(own.file, own.line, `${key}: number of #! differs from ${REF_LANG}`);
      if (count(own.text, /\\n/g) !== count(ref.text, /\\n/g)) error(own.file, own.line, `${key}: number of \\n differs from ${REF_LANG}`);
    }
    for (const [key, own] of table) if (!loc.has(key)) error(own.file, own.line, `${lang} localization key ${key} is not in ${REF_LANG}`);
  }
  const used = new Set();
  for (const ref of locRefs) {
    used.add(ref.key);
    if (!loc.has(ref.key)) error(ref.file, ref.line, `missing localization key ${ref.key}`);
  }
  for (const [key, entry] of loc) if (!used.has(key)) report('warning', entry.file, entry.line, `localization key ${key} is never used`);

  problems.sort((a, b) => a.file.localeCompare(b.file) || a.line - b.line || a.message.localeCompare(b.message));
  const errors = problems.filter((p) => p.level === 'error').length;
  const warnings = problems.filter((p) => p.level === 'warning').length;
  const summary = `checked ${files.length} files, ${loc.size} localization keys, ${MODDEF.effects.size} scripted effects, ` +
    `${MODDEF.triggers.size} scripted triggers, ${MODDEF.events.size} events: ${errors} error(s), ${warnings} warning(s)`;
  return { problems, errors, warnings, summary };
}

function main() {
  const args = process.argv.slice(2);
  const modDir = args.includes('--mod') ? args[args.indexOf('--mod') + 1] : DEFAULT_MOD;
  const { problems, errors, summary } = lintMod({ modDir });
  for (const p of problems) console.log(`${p.level}: ${p.file}:${p.line}: ${p.message}`);
  console.log(summary);
  process.exitCode = errors ? 1 : 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
