using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Jobs.Tests;

public sealed class JobSignalTests
{
    [Fact]
    public void PulseWhileAWakeUpIsPending_ThrowsNoException_AndStillWakesTheWaiter()
    {
        var signal = new JobSignal();
        int thrown = 0;
        int thread = Environment.CurrentManagedThreadId;
        EventHandler<FirstChanceExceptionEventArgs> count = (_, _) =>
        {
            if (Environment.CurrentManagedThreadId == thread)
            {
                thrown++;
            }
        };

        AppDomain.CurrentDomain.FirstChanceException += count;
        try
        {
            signal.Pulse();
            signal.Pulse();
            signal.Pulse();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= count;
        }

        Assert.Equal(0, thrown);
        Assert.True(signal.WaitAsync(TimeSpan.Zero, CancellationToken.None).IsCompletedSuccessfully);
    }
}
