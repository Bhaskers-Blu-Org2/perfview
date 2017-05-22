﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.VisualStudio.Threading;
using PerfView;
using PerfViewTests.Utilities;
using Xunit;
using DataGridCellInfo = System.Windows.Controls.DataGridCellInfo;

namespace PerfViewTests.StackViewer
{
    public class StackWindowTests
    {
        [WpfFact]
        [WorkItem(235, "https://github.com/Microsoft/perfview/issues/235")]
        [UseCulture("en-US")]
        public void TestSetTimeRange()
        {
            TestSetTimeRangeWithSpaceImpl(CultureInfo.CurrentCulture);
        }

        [WpfFact]
        [WorkItem(235, "https://github.com/Microsoft/perfview/issues/235")]
        [UseCulture("ru-RU")]
        public void TestSetTimeRangeWithSpace()
        {
            TestSetTimeRangeWithSpaceImpl(CultureInfo.CurrentCulture);
        }

        private void TestSetTimeRangeWithSpaceImpl(CultureInfo culture)
        {
            // Create the main application
            AppLog.s_IsUnderTest = true;
            App.CommandLineArgs = new CommandLineArgs();
            App.CommandProcessor = new CommandProcessor();
            GuiApp.MainWindow = new MainWindow();
            var factory = new JoinableTaskFactory(new JoinableTaskContext());

            // Create the controls
            var stackWindowTask = factory.RunAsync(async () =>
            {
                await factory.SwitchToMainThreadAsync();

                // The main window has to be visible or the Closing event will not be raised on owned windows.
                GuiApp.MainWindow.Show();

                var file = new TimeRangeFile();
                await OpenAsync(factory, file, GuiApp.MainWindow, GuiApp.MainWindow.StatusBar).ConfigureAwait(true);
                var stackSource = file.GetStackSource();
                return stackSource.Viewer;
            });

            // Create the dispatcher for a UI thread
            var frame = new DispatcherFrame();

            // Launch a background thread to drive interaction with the controls
            TaskCompletionSource<VoidResult> readyTrigger = new TaskCompletionSource<VoidResult>();
            var testDriver = factory.RunAsync(async () =>
            {
                await readyTrigger.Task.ConfigureAwait(false);

                // Show the window
                var stackWindow = await stackWindowTask.Task.ConfigureAwait(false);

                try
                {
                    await factory.SwitchToMainThreadAsync();

                    var byNameView = stackWindow.m_byNameView;
                    var row = byNameView.FindIndex(node => node.FirstTimeRelativeMSec > 0 && node.FirstTimeRelativeMSec < node.LastTimeRelativeMSec);
                    CallTreeNodeBase selected = byNameView[row];

                    var selectedCells = stackWindow.ByNameDataGrid.Grid.SelectedCells;
                    selectedCells.Clear();
                    selectedCells.Add(new DataGridCellInfo(byNameView[row], stackWindow.ByNameDataGrid.FirstTimeColumn));
                    selectedCells.Add(new DataGridCellInfo(byNameView[row], stackWindow.ByNameDataGrid.LastTimeColumn));

                    StackWindow.SetTimeRangeCommand.Execute(null, stackWindow.ByNameDataGrid);

                    // Yield so command takes effect
                    await Task.Delay(20);

                    Assert.Equal(selected.FirstTimeRelativeMSec.ToString("n3", culture), stackWindow.StartTextBox.Text);
                    Assert.Equal(selected.LastTimeRelativeMSec.ToString("n3", culture), stackWindow.EndTextBox.Text);

                    stackWindow.Close();
                }
                finally
                {
                    await factory.SwitchToMainThreadAsync();
                    await Task.Yield();
                    frame.Continue = false;
                }
            }, JoinableTaskCreationOptions.LongRunning);

            readyTrigger.SetResult(default(VoidResult));

            Dispatcher.PushFrame(frame);

            // Ensure failures in testDriver cause the whole test to fail
            testDriver.Join();

            GuiApp.MainWindow.Close();
        }

        private static Task OpenAsync(JoinableTaskFactory factory, PerfViewTreeItem item, Window parentWindow, StatusBar worker)
        {
            return factory.RunAsync(async () =>
            {
                await factory.SwitchToMainThreadAsync();

                var result = new TaskCompletionSource<VoidResult>();
                item.Open(parentWindow, worker, () => result.SetResult(default(VoidResult)));
                await result.Task.ConfigureAwait(false);
            }).Task;
        }

        /// <summary>
        /// A simple file containing four events at different points of time in order to test filtering.
        /// </summary>
        private class TimeRangeFile : PerfViewFile
        {
            public override string Title => nameof(TimeRangeFile);

            public override string FormatName => Title;

            public override string[] FileExtensions => new[] { "Time Range Test" };

            protected override Action<Action> OpenImpl(Window parentWindow, StatusBar worker)
            {
                return doAfter =>
                {
                    m_singletonStackSource = new PerfViewStackSource(this, string.Empty);
                    m_singletonStackSource.Open(parentWindow, worker, doAfter);
                };
            }

            protected internal override StackSource OpenStackSourceImpl(TextWriter log)
            {
                return new TimeRangeStackSource();
            }
        }

        private class TimeRangeStackSource : StackSource
        {
            private readonly List<StackSourceSample> _samples;

            private const StackSourceCallStackIndex StackRoot = StackSourceCallStackIndex.Invalid;
            private const StackSourceCallStackIndex StackEntry = StackSourceCallStackIndex.Start + 0;
            private const StackSourceCallStackIndex StackMiddle = StackSourceCallStackIndex.Start + 1;
            private const StackSourceCallStackIndex StackTail = StackSourceCallStackIndex.Start + 2;

            private const StackSourceFrameIndex FrameEntry = StackSourceFrameIndex.Start + 0;
            private const StackSourceFrameIndex FrameMiddle = StackSourceFrameIndex.Start + 1;
            private const StackSourceFrameIndex FrameTail = StackSourceFrameIndex.Start + 2;

            public TimeRangeStackSource()
            {
                _samples = new List<StackSourceSample>
                {
                    new StackSourceSample(this)
                    {
                        SampleIndex = 0,
                        StackIndex = StackEntry,
                        Metric = 1,
                        TimeRelativeMSec = 0
                    },
                    new StackSourceSample(this)
                    {
                        SampleIndex = (StackSourceSampleIndex)1,
                        StackIndex = StackMiddle,
                        Metric = 1,
                        TimeRelativeMSec = 1000.25
                    },
                    new StackSourceSample(this)
                    {
                        SampleIndex = (StackSourceSampleIndex)2,
                        StackIndex = StackMiddle,
                        Metric = 1,
                        TimeRelativeMSec = 2000.5
                    },
                    new StackSourceSample(this)
                    {
                        SampleIndex = (StackSourceSampleIndex)3,
                        StackIndex = StackTail,
                        Metric = 1,
                        TimeRelativeMSec = 3000.75
                    },
                };
            }

            public override int CallStackIndexLimit
                => (int)StackTail + 1;

            public override int CallFrameIndexLimit
                => (int)FrameTail + 1;

            public override int SampleIndexLimit
                => _samples.Count;

            public override double SampleTimeRelativeMSecLimit
                => _samples.Select(sample => sample.TimeRelativeMSec).Max();

            public override void ForEach(Action<StackSourceSample> callback)
            {
                _samples.ForEach(callback);
            }

            public override StackSourceSample GetSampleByIndex(StackSourceSampleIndex sampleIndex)
                => _samples[(int)sampleIndex];

            public override StackSourceCallStackIndex GetCallerIndex(StackSourceCallStackIndex callStackIndex)
            {
                return StackSourceCallStackIndex.Invalid;
            }

            public override StackSourceFrameIndex GetFrameIndex(StackSourceCallStackIndex callStackIndex)
            {
                switch (callStackIndex)
                {
                    case StackEntry:
                        return FrameEntry;

                    case StackMiddle:
                        return FrameMiddle;

                    case StackTail:
                        return FrameTail;

                    default:
                        return StackSourceFrameIndex.Root;
                }
            }

            public override string GetFrameName(StackSourceFrameIndex frameIndex, bool verboseName)
            {
                if (frameIndex >= StackSourceFrameIndex.Start)
                    return (frameIndex - StackSourceFrameIndex.Start).ToString();

                return frameIndex.ToString();
            }
        }
    }
}
