namespace Nao.Loader.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Loader

[<TestClass>]
type RuntimeTests() =

    // ─── ToolRuntime.resolve ───

    [<TestMethod>]
    member _.ResolveNativeForEmpty() =
        let r = ToolRuntime.resolve ""
        Assert.AreEqual("native", r.Name)
        Assert.AreEqual("", r.Command)

    [<TestMethod>]
    member _.ResolveDenoBuiltin() =
        let r = ToolRuntime.resolve "deno"
        Assert.AreEqual("deno", r.Command)
        Assert.AreEqual(["run"; "-A"], r.Args)

    [<TestMethod>]
    member _.ResolveIsCaseInsensitive() =
        let r = ToolRuntime.resolve "Deno"
        Assert.AreEqual("deno", r.Command)

    [<TestMethod>]
    member _.ResolveUnknownTreatsNameAsLauncher() =
        let r = ToolRuntime.resolve "my-runner"
        Assert.AreEqual("my-runner", r.Command)
        Assert.AreEqual<string list>([], r.Args)

    // ─── RuntimePolicy.parse ───

    [<TestMethod>]
    member _.ParseEmptyIsHostDefault() =
        Assert.AreEqual(RuntimePolicy.HostDefault, RuntimePolicy.parse "")

    [<TestMethod>]
    member _.ParseDockerIsContainerizedNone() =
        Assert.AreEqual(RuntimePolicy.Containerized None, RuntimePolicy.parse "docker")

    [<TestMethod>]
    member _.ParseDockerWithImage() =
        Assert.AreEqual(RuntimePolicy.Containerized (Some "node:20"), RuntimePolicy.parse "docker:node:20")

    [<TestMethod>]
    member _.ParseRuntimeName() =
        Assert.AreEqual(RuntimePolicy.ForceRuntime "deno", RuntimePolicy.parse "deno")

    // ─── DefinitionBuilder.resolveProcessLauncher ───

    [<TestMethod>]
    member _.NativeToolRunsCommandDirectly() =
        let (launcher, args) =
            DefinitionBuilder.resolveProcessLauncher RuntimePolicy.HostDefault "" "echo"
        Assert.AreEqual("echo", launcher)
        Assert.AreEqual<string list>([], args)

    [<TestMethod>]
    member _.DenoToolWrapsCommand() =
        let (launcher, args) =
            DefinitionBuilder.resolveProcessLauncher RuntimePolicy.HostDefault "deno" "tool.ts"
        Assert.AreEqual("deno", launcher)
        Assert.AreEqual(["run"; "-A"; "tool.ts"], args)

    [<TestMethod>]
    member _.ForceRuntimeOverridesToolRuntime() =
        // Tool declares native, session forces ts-node.
        let (launcher, args) =
            DefinitionBuilder.resolveProcessLauncher (RuntimePolicy.ForceRuntime "ts-node") "" "tool.ts"
        Assert.AreEqual("ts-node", launcher)
        Assert.AreEqual(["tool.ts"], args)

    [<TestMethod>]
    member _.ContainerizedUsesRuntimeDefaultImage() =
        // Deno tool containerized with no explicit image -> deno's default image.
        let (launcher, args) =
            DefinitionBuilder.resolveProcessLauncher (RuntimePolicy.Containerized None) "deno" "tool.ts"
        Assert.AreEqual("docker", launcher)
        Assert.IsTrue(List.contains "denoland/deno:latest" args)
        // The wrapped runtime command is still present inside the container invocation.
        Assert.IsTrue(List.contains "deno" args)
        Assert.IsTrue(List.contains "tool.ts" args)

    [<TestMethod>]
    member _.ContainerizedUsesExplicitImage() =
        let (launcher, args) =
            DefinitionBuilder.resolveProcessLauncher (RuntimePolicy.Containerized (Some "node:20")) "" "tool.js"
        Assert.AreEqual("docker", launcher)
        Assert.IsTrue(List.contains "node:20" args)
        Assert.IsTrue(List.contains "tool.js" args)
