// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Headless;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// One Avalonia UI thread, shared by every render test in the assembly.
///
/// Avalonia binds its dispatcher to whichever thread set the platform up, and
/// controls may only be built and drawn on that thread — but xunit hands each
/// test whatever pool thread is free. So the fixture owns a thread of its own,
/// starts the real application on it once, and marshals each test body across.
/// </summary>
/// <remarks>
/// Avalonia ships an xunit integration that would do this, but at 12.1 it is
/// built against xunit v3 while the rest of the repo's tests are on v2; mixed
/// in one solution, its attributes are simply never discovered. Running the
/// dispatcher here costs about forty lines and keeps one xunit across the
/// repo.
/// </remarks>
public sealed class HeadlessAvalonia : IDisposable
{
    public const string CollectionName = "avalonia";

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public HeadlessAvalonia()
    {
        using var ready = new ManualResetEventSlim();
        ExceptionDispatchInfo? startupFailure = null;

        _thread = new Thread(() =>
        {
            try
            {
                // The real App, so tests draw against the app's own theme and
                // brushes. Skia rather than the headless stub renderer: the
                // point is to sample pixels, and the stub draws none.
                Trace.Listeners.Add(RenderProblems.Listener);

                AppBuilder.Configure<App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
                    .SetupWithoutStarting();
            }
            catch (Exception ex)
            {
                startupFailure = ExceptionDispatchInfo.Capture(ex);
                return;
            }
            finally
            {
                ready.Set();
            }

            foreach (var job in _work.GetConsumingEnumerable()) job();
        })
        {
            IsBackground = true,
            Name = "Avalonia UI (tests)",
        };

        _thread.Start();
        ready.Wait();
        startupFailure?.Throw();
    }

    /// <summary>Runs a test body on the UI thread and brings any failure back
    /// with its stack intact, so a broken assertion reads as itself rather than
    /// as a marshalling error.</summary>
    public void Run(Action body)
    {
        using var done = new ManualResetEventSlim();
        ExceptionDispatchInfo? failure = null;

        _work.Add(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        });

        done.Wait();
        failure?.Throw();
    }

    public void Dispose() => _work.CompleteAdding();
}

[CollectionDefinition(HeadlessAvalonia.CollectionName)]
public sealed class HeadlessAvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;

/// <summary>Base for anything that needs to draw. Every test body runs on the
/// shared UI thread.</summary>
[Collection(HeadlessAvalonia.CollectionName)]
public abstract class RenderTest
{
    private readonly HeadlessAvalonia _ui;

    protected RenderTest(HeadlessAvalonia ui) => _ui = ui;

    /// <summary>Runs the body on the UI thread, and fails if anything drawn
    /// during it logged a problem.
    ///
    /// Avalonia catches whatever a <c>Render</c> override throws and carries on
    /// with the next control, so a chart that dies halfway simply comes out
    /// missing its later layers. Without this the test would report only that
    /// some colour was absent, which says nothing about why.</summary>
    protected void Ui(Action body) => _ui.Run(() =>
    {
        RenderProblems.Clear();
        body();
        RenderProblems.AssertNone();
    });
}

/// <summary>Collects the warnings and errors Avalonia logs while drawing.
/// </summary>
public sealed class RenderProblems : TraceListener
{
    private static readonly List<string> Messages = [];

    public static readonly RenderProblems Listener = new();

    public override void Write(string? message) => Record(message);

    public override void WriteLine(string? message) => Record(message);

    private static void Record(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (Messages) Messages.Add(message);
    }

    public static void Clear()
    {
        lock (Messages) Messages.Clear();
    }

    public static void AssertNone()
    {
        string[] found;
        lock (Messages) found = [.. Messages];

        Assert.True(found.Length == 0,
            "drawing logged a problem: " + string.Join(" | ", found.Take(4)));
    }
}
