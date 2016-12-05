using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

using Xunit;

using Http2;

namespace Http2Tests
{
    public class GeneralTests
    {
        [Fact]
        public async Task TimersShouldBeCancellable()
        {
            // This is just a sanity check for the platform, that checks whether
            // timers through Task.Delay are cancellable.
            var cts = new CancellationTokenSource();
            var sw = new Stopwatch();
            sw.Start();
            var task = Task.Delay(100, cts.Token);
            cts.Cancel();
            cts.Dispose();
            try
            {
                await task;
                sw.Stop();
                Assert.False(true, "Cancelled task should not succeed");
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
            }
            Assert.True(task.IsCanceled);
            // Check how fast the cancellation is processed
            // If the timers of the platform are far off this
            // might produce errors.
            Assert.True(sw.ElapsedMilliseconds < 20);
        }
    }
}