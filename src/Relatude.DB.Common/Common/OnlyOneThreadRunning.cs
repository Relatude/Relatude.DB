namespace Relatude.DB.Common;
/// <summary>
/// A utility class to ensure that only one thread can run a specific operation at a time.
/// Similar to a spinlock, but uses Interlocked operations to avoid busy-waiting.
/// </summary>
public class OnlyOneThreadRunning {
    private int _runningFlag = 0; // 0 = not running, 1 = running

    // Checks if already running. If not, sets it to running and returns false.
    // If already running, returns true.
    public bool IsRunning_IfNotSetFlagToRunning() {
        // Try to set from 0 (not running) to 1 (running)
        int original = Interlocked.CompareExchange(ref _runningFlag, 1, 0);
        return original == 1; // true if already running
    }

    // Sets to running. Throws if already running.
    public void FlagToRun_ThrowIfAlreadyRunning() {
        // Try to set from 0 to 1
        if (Interlocked.CompareExchange(ref _runningFlag, 1, 0) != 0) {
            throw new InvalidOperationException("Another thread is already running. Please wait until it completes before starting a new operation.");
        }
    }

    // Resets the flag to allow other threads to run
    public void Reset() {
        // Set to not running
        Interlocked.Exchange(ref _runningFlag, 0);
    }
}
