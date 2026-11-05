# Code to Life

See a C# codebase as a living map: every class and how it connects, the methods inside and who calls them, and, while it runs, where the program actually is.

Point it at a solution, project or folder and it opens a browser page with:

- **The map.** Every type, grouped by namespace, joined by how they really relate: inherits, implements, *has* (a field or property holds it), *calls*, *creates* (`new`), and optionally *mentions*. Click a class for its members, dependencies, users and source. Double-click to open it up and see its methods calling each other and into other classes. Right-click a method to follow its calls a few levels deep.
- **The debugger, drawn on the map.** Start your program (or attach to one) and step through it. Whenever it pauses, the call stack lights up across the graph as a trail of glowing nodes and moving arrows, with locals and the current line beside it. **Animate** keeps stepping on its own so you can watch execution flow through the classes. Click the margin of the source view, or a method's ● button, for breakpoints. **Run to here** on any method.
- **Live view, no debugger needed.** Pick any running .NET process and Code to Life samples its stacks several times a second (without pausing it) and heats up the code that's running. Calls that only exist at runtime, like the concrete class behind an interface, appear as new cyan edges. **Count instances** shows how many objects of each class are alive on the heap.
- **What changed.** Uses [PR Buddy](https://github.com/anefiox/pr-buddy)'s semantic diff to color the members your branch really changed (behavior, signature, added, removed), ignoring reformatting, with PR Buddy's findings pinned to the code they're about.
- **Plan mode.** Sketch new types on top of the real graph, connect them to existing classes, leave notes on anything, and turn the sketch into C# stubs. The plan is saved in `.codetolife/plan.json` beside your code, so it can be committed and reviewed.
- **Color by** kind, project, complexity, coupling, changes or live activity. The picture re-analyzes itself whenever a `.cs` or `.csproj` file changes.

Nothing leaves your machine: the server listens on localhost only and refuses API calls from other sites.

## Run it

Needs the .NET 10 SDK.

```bash
dotnet run --project src/CodeToLife -- path/to/YourSolution.sln
```

The argument can be a `.sln`, `.slnx`, `.csproj` or a folder (default: the current directory). It opens your browser at `http://localhost:5170`. Options: `--port <n>`, `--no-browser`.

Try it on the bundled sample, a little shop that keeps taking orders:

```bash
dotnet build samples/SampleShop
dotnet run --project src/CodeToLife -- samples/SampleShop
```

Then, in the page:

1. Double-click **OrderService** to see its methods and what they call.
2. **Live** tab: run `samples/SampleShop/bin/Debug/net10.0/SampleShop.exe` in a terminal, click ⟳, pick the ★ process, **Watch**. The recommendation engine glows as it burns CPU; **Count instances** shows orders piling up.
3. **Debug** tab (needs netcoredbg, below): click OrderService's `CheckoutAsync` → **● Breakpoint**, then **▶ Start** with arguments `--once`. When it stops, the stack lights up from the top-level code through `OrderService`. Press **▶▶ Animate** to watch it step through the pricing rules.

To install it as a global tool:

```bash
dotnet pack src/CodeToLife -c Release -o nupkg
dotnet tool install -g CodeToLife --add-source ./nupkg
codetolife path/to/YourSolution.sln
```

## The debugger

Stepping uses **netcoredbg**, Samsung's open-source (MIT) .NET debugger, over the Debug Adapter Protocol, the same protocol VS Code speaks. (Microsoft's `vsdbg` isn't used: its license only allows Microsoft's own tools.) Install it with:

```bash
pwsh tools/Get-Netcoredbg.ps1
```

That puts it in `tools/netcoredbg`, where Code to Life looks first (tested with netcoredbg 3.2.0 on Windows). It also checks the `CODETOLIFE_NETCOREDBG` environment variable and your `PATH`.

The **Live** view doesn't need it: it reads stacks with [ClrMD](https://github.com/microsoft/clrmd) from a snapshot of the process.

Shortcuts: <kbd>F5</kbd> start / continue, <kbd>F10</kbd> step over, <kbd>F11</kbd> step into, <kbd>Shift</kbd>+<kbd>F11</kbd> step out, <kbd>Ctrl</kbd>+<kbd>K</kbd> search, <kbd>Esc</kbd> back out, <kbd>F</kbd> fit.

The **Build** button runs `dotnet build -c Debug` on the chosen project, so the debugger runs the code you're looking at. **Open in editor** links use VS Code (`code --goto`); set `CODETOLIFE_EDITOR` to use something else that understands `--goto file:line`.

## What changed (PR Buddy)

The changes overlay reuses PR Buddy's `GitDiffReader`, `SemanticDiffer` and rules. It's linked in automatically when PR Buddy is cloned next to this repository:

```
Code/
  code-to-life/
  pr-buddy/        ← https://github.com/anefiox/pr-buddy
```

Elsewhere, build with `-p:PrBuddyPath=/path/to/pr-buddy`. Without it everything else still works and the tab explains how to turn it on.

In the **What changed** tab, pick a base branch (default: `origin/main`, `origin/master`, `main` or `master`, compared from the merge base like GitHub) and whether to include uncommitted work.

## How it works

```
src/
  CodeToLife.Analysis   Roslyn: projects → compilations → types, members, edges, calls
  CodeToLife.Runtime    DAP client + debug session (netcoredbg), ClrMD live sampler,
                        and FrameMapper, which maps stack frames back to graph nodes
  CodeToLife            ASP.NET Core host: JSON API, Server-Sent Events, file watcher,
                        PR Buddy overlay, plan store; wwwroot is the page (Cytoscape.js)
samples/SampleShop      A small app to explore and debug
tests/CodeToLife.Tests  Graph building, frame mapping and the DAP wire format
```

**Analysis doesn't run MSBuild.** Design-time builds are slow, need a matching SDK, and fail on half-written code, which is exactly when you want a picture. Instead each project is compiled straight from its `.cs` files with Roslyn, referenced projects first, against the running .NET runtime plus its NuGet packages (from `obj/project.assets.json` after a restore, otherwise from the global package cache, following dependencies) and anything already in its `bin` folder. Every type declared in source resolves, which is all the graph needs. If packages can't be found, the sidebar says how many compile errors that caused; types still show, and `dotnet restore` fills in the missing calls.

**Mapping a running program back to the source** goes two ways. The debugger reports file and line, mapped to the innermost member whose span contains it. The live sampler reports CLR names like `OrderService+<CheckoutAsync>d__6.MoveNext` or `Program+<>c.<<Main>$>b__0_0`; `FrameMapper` sees through the classes the compiler generates for async methods, iterators, lambdas, closures and local functions, and handles generics, nested types, constructors and property accessors.

Complexity is counted the same way as PR Buddy's PRB013 rule: 1 plus one per branch point.

## Limits

- C# only. F# and VB projects are skipped.
- Source generators don't run, so types they'd generate are missing (and `[GeneratedRegex]` methods show as unimplemented, harmlessly).
- Live view sees code that's *running*: an `async` method waiting on I/O isn't on any stack until it resumes. Sampling also misses very short calls; leave it running a while.
- Very large solutions (thousands of types) render, but are easier to read with test projects hidden, namespaces filtered, or **Focus** on one class.

## Development

```bash
dotnet test
```

Front-end libraries (Cytoscape.js, cytoscape-fcose, cytoscape-dagre and their dependencies, all MIT) are vendored in `src/CodeToLife/wwwroot/lib`, so the page works offline.
