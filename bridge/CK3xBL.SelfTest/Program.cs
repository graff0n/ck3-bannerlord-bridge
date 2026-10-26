// Shared self-test entry point. Each component registers its suite here with ONE line (see TestRegistry.cs).
// Run: dotnet run --project CK3xBL.SelfTest [-- <name filter> ...]   (exit code = number of failures)
using CK3xBL.SelfTest;

var registry = new TestRegistry();

CoreTests.Register(registry);
UxTests.Register(registry);
MappingTests.Register(registry);
AppearanceTests.Register(registry);
IntegrationTests.Register(registry);
ReleaseTests.Register(registry);
I18nTests.Register(registry);
// <Component>Tests.Register(registry);   <- other components add their line here

return await registry.RunAsync(args);
