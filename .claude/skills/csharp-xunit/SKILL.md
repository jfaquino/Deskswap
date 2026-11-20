---
name: csharp-xunit
description: 'Get best practices for XUnit unit testing, including data-driven tests'
---

# XUnit Best Practices

Your goal is to help me write effective unit tests with XUnit, covering both standard and data-driven testing approaches.

## Project Setup

- Use a separate test project with naming convention `[ProjectName].Tests`
- This repo uses **xUnit v3 on Microsoft Testing Platform** (opted in via `"test": { "runner": "Microsoft.Testing.Platform" }` in `global.json`):
  - Reference only the `xunit.v3` package (version in `Directory.Packages.props`, no `Version` in the csproj)
  - Do **not** add `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `xunit` (v2) or `coverlet.collector` — they are VSTest-only and break `dotnet test` on the .NET 10 SDK
  - Test projects set `<OutputType>Exe</OutputType>`
- Create test classes that match the classes being tested (e.g., `CalculatorTests` for `Calculator`)
- Use .NET SDK test commands: `dotnet test` for running tests

## Test Structure

- No test class attributes required (unlike MSTest/NUnit)
- Use fact-based tests with `[Fact]` attribute for simple tests
- Follow the Arrange-Act-Assert (AAA) pattern
- Name tests using the pattern `MethodName_Scenario_ExpectedBehavior` (underscores are allowed under `tests/` by `.editorconfig`)
- Use constructor for setup and `IDisposable.Dispose()` for teardown (or `IAsyncLifetime`, which returns `ValueTask` in v3)
- Use `IClassFixture<T>` for shared context between tests in a class
- Use `ICollectionFixture<T>` for shared context between multiple test classes

## Standard Tests

- Keep tests focused on a single behavior
- Avoid testing multiple behaviors in one test method
- Use clear assertions that express intent
- Include only the assertions needed to verify the test case
- Make tests independent and idempotent (can run in any order)
- Avoid test interdependencies

## Data-Driven Tests

- Use `[Theory]` combined with data source attributes
- Use `[InlineData]` for inline test data
- Use `[MemberData]` for method-based test data
- Use `[ClassData]` for class-based test data
- Create custom data attributes by implementing `DataAttribute`
- Use meaningful parameter names in data-driven tests

## Assertions

- Use `Assert.Equal` for value equality
- Use `Assert.Same` for reference equality
- Use `Assert.True`/`Assert.False` for boolean conditions
- Use `Assert.Contains`/`Assert.DoesNotContain` for collections
- Use `Assert.Matches`/`Assert.DoesNotMatch` for regex pattern matching
- Use `Assert.Throws<T>` or `await Assert.ThrowsAsync<T>` to test exceptions
- Use the built-in `Assert` API; don't add FluentAssertions (commercial license since v8)

## Mocking and Isolation

- Prefer small hand-written fakes for the interfaces Core depends on; reach for NSubstitute only when a fake gets complicated
- Use interfaces to facilitate isolation
- Don't use a DI container in tests

## Test Organization

- Group tests by feature or component
- Use `[Trait("Category", "CategoryName")]` for categorization
- Use collection fixtures to group tests with shared dependencies
- Consider output helpers (`ITestOutputHelper`, or `TestContext.Current.TestOutputHelper` in v3) for test diagnostics
- Skip tests with `Skip = "reason"` in fact/theory attributes, or `Assert.Skip(...)` / `Assert.SkipWhen(...)` at runtime in v3
- Pass `TestContext.Current.CancellationToken` to async calls in tests
