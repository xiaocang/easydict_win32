using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for TranslationManagerService.
/// Note: TranslationManagerService is a singleton that manages the shared TranslationManager.
/// These tests verify singleton behavior, service configuration, and thread safety.
/// </summary>
[Trait("Category", "WinUI")]
public class TranslationManagerServiceTests
{
    [Fact]
    public void Instance_ReturnsSameInstance()
    {
        var instance1 = TranslationManagerService.Instance;
        var instance2 = TranslationManagerService.Instance;

        instance1.Should().BeSameAs(instance2);
    }

    [Fact]
    public void Manager_IsNotNull()
    {
        var service = TranslationManagerService.Instance;

        service.Manager.Should().NotBeNull();
    }

    [Fact]
    public void Manager_HasRegisteredServices()
    {
        var service = TranslationManagerService.Instance;
        var manager = service.Manager;

        manager.Services.Should().NotBeEmpty();
        manager.Services.Should().ContainKey("google");
    }

    [Fact]
    public void Manager_HasDefaultServiceId()
    {
        var service = TranslationManagerService.Instance;
        var manager = service.Manager;

        manager.DefaultServiceId.Should().NotBeNullOrEmpty();
        manager.Services.Should().ContainKey(manager.DefaultServiceId);
    }

    [Fact]
    public void ReconfigureServices_DoesNotChangeManagerInstance()
    {
        var service = TranslationManagerService.Instance;
        var managerBefore = service.Manager;

        service.ReconfigureServices();

        var managerAfter = service.Manager;
        managerAfter.Should().BeSameAs(managerBefore);
    }

    [Fact]
    public void ReconfigureProxy_CreatesNewManagerInstance()
    {
        var service = TranslationManagerService.Instance;
        var managerBefore = service.Manager;

        service.ReconfigureProxy();

        var managerAfter = service.Manager;
        managerAfter.Should().NotBeSameAs(managerBefore);
    }

    [Fact]
    public void ReconfigureProxy_NewManagerHasRegisteredServices()
    {
        var service = TranslationManagerService.Instance;

        service.ReconfigureProxy();

        var manager = service.Manager;
        manager.Services.Should().NotBeEmpty();
        manager.Services.Should().ContainKey("google");
    }

    [Fact]
    public void Manager_CanBeAccessedConcurrently()
    {
        var service = TranslationManagerService.Instance;
        var exceptions = new List<Exception>();
        var managerReferences = new List<object>();
        var lockObj = new object();

        // Access Manager from multiple threads concurrently
        Parallel.For(0, 100, _ =>
        {
            try
            {
                var manager = service.Manager;
                lock (lockObj)
                {
                    managerReferences.Add(manager);
                }
            }
            catch (Exception ex)
            {
                lock (lockObj)
                {
                    exceptions.Add(ex);
                }
            }
        });

        exceptions.Should().BeEmpty("concurrent access should not throw exceptions");
        managerReferences.Should().HaveCount(100);
        // All references should be to the same manager instance
        managerReferences.Distinct().Should().HaveCount(1);
    }

    [Fact]
    public void ReconfigureServices_CanBeCalledConcurrently()
    {
        var service = TranslationManagerService.Instance;
        var exceptions = new List<Exception>();
        var lockObj = new object();

        // Call ReconfigureServices from multiple threads
        Parallel.For(0, 10, _ =>
        {
            try
            {
                service.ReconfigureServices();
            }
            catch (Exception ex)
            {
                lock (lockObj)
                {
                    exceptions.Add(ex);
                }
            }
        });

        exceptions.Should().BeEmpty("concurrent reconfiguration should not throw exceptions");
        service.Manager.Should().NotBeNull();
    }

    [Fact]
    public void AcquireHandle_ReturnsValidHandle()
    {
        var service = TranslationManagerService.Instance;

        using var handle = service.AcquireHandle();

        handle.Should().NotBeNull();
        handle.Manager.Should().NotBeNull();
        handle.Manager.Should().BeSameAs(service.Manager);
    }

    [Fact]
    public void SafeHandle_ThrowsWhenAccessedAfterDispose()
    {
        var service = TranslationManagerService.Instance;
        var handle = service.AcquireHandle();

        handle.Dispose();

        var act = () => { var _ = handle.Manager; };
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void SafeHandle_PreventsDisposalDuringUse()
    {
        var service = TranslationManagerService.Instance;

        // Acquire handle before reconfiguring
        using var handle = service.AcquireHandle();
        var managedManager = handle.Manager;

        // Reconfigure proxy (would normally queue old manager for disposal)
        service.ReconfigureProxy();

        // The handle should still provide access to the original manager
        // and it should not be disposed
        var act = () =>
        {
            // Access the manager through the handle - should not throw
            var _ = handle.Manager.Services;
        };
        act.Should().NotThrow("manager should not be disposed while handle is held");

        // The current service manager should be the new one
        service.Manager.Should().NotBeSameAs(managedManager);
    }

    [Fact]
    public void AcquireHandle_ThreadSafe_MultipleHandles()
    {
        var service = TranslationManagerService.Instance;
        var exceptions = new List<Exception>();
        var handles = new List<SafeManagerHandle>();
        var lockObj = new object();

        // Acquire handles from multiple threads concurrently
        Parallel.For(0, 50, _ =>
        {
            try
            {
                var handle = service.AcquireHandle();
                lock (lockObj)
                {
                    handles.Add(handle);
                }
            }
            catch (Exception ex)
            {
                lock (lockObj)
                {
                    exceptions.Add(ex);
                }
            }
        });

        exceptions.Should().BeEmpty("concurrent handle acquisition should not throw exceptions");
        handles.Should().HaveCount(50);

        // All handles should reference a valid manager
        foreach (var handle in handles)
        {
            handle.Manager.Should().NotBeNull();
        }

        // Dispose all handles
        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }

    [Fact]
    public void ReconfigureProxy_WithActiveHandle_QueuesForDisposal()
    {
        var service = TranslationManagerService.Instance;

        // Acquire a handle
        var handle = service.AcquireHandle();
        var originalManager = handle.Manager;

        // Reconfigure - old manager should be queued, not disposed
        service.ReconfigureProxy();

        // Original manager should still be usable through the handle
        originalManager.Services.Should().NotBeEmpty("manager should not be disposed while handle exists");

        // Release the handle
        handle.Dispose();

        // The original manager is no longer the current one
        service.Manager.Should().NotBeSameAs(originalManager);
    }

    [Fact]
    public void MultipleHandles_AllPreventDisposal()
    {
        var service = TranslationManagerService.Instance;

        // Acquire multiple handles
        var handle1 = service.AcquireHandle();
        var handle2 = service.AcquireHandle();
        var handle3 = service.AcquireHandle();

        var originalManager = handle1.Manager;

        // Reconfigure proxy
        service.ReconfigureProxy();

        // All handles should still work
        handle1.Manager.Should().BeSameAs(originalManager);
        handle2.Manager.Should().BeSameAs(originalManager);
        handle3.Manager.Should().BeSameAs(originalManager);

        // Dispose handles one by one
        handle1.Dispose();
        handle2.Manager.Should().BeSameAs(originalManager); // Still valid

        handle2.Dispose();
        handle3.Manager.Should().BeSameAs(originalManager); // Still valid

        handle3.Dispose();
        // Now original manager can be disposed (happens asynchronously)
    }

    [Fact]
    public void ReconfigureProxy_DuringSimulatedStreaming_ManagerRemainsValid()
    {
        var service = TranslationManagerService.Instance;

        // Simulate a long-running streaming operation by holding a handle
        using var handle = service.AcquireHandle();
        var originalManager = handle.Manager;

        // Perform multiple proxy reconfigurations while "streaming"
        for (int i = 0; i < 3; i++)
        {
            service.ReconfigureProxy();
        }

        // The handle should still provide access to the original manager
        // and the manager should still be functional
        var act = () =>
        {
            handle.Manager.Services.Should().NotBeEmpty();
            handle.Manager.Should().BeSameAs(originalManager);
        };
        act.Should().NotThrow("manager should remain valid during simulated streaming");

        // Current service manager should be the latest reconfigured one
        service.Manager.Should().NotBeSameAs(originalManager);
    }

    [Fact]
    public async Task ReconfigureProxy_ConcurrentWithAcquireHandle_NoExceptions()
    {
        var service = TranslationManagerService.Instance;
        var exceptions = new List<Exception>();
        var handles = new List<SafeManagerHandle>();
        var lockObj = new object();
        var startBarrier = new Barrier(20); // 10 handle acquirers + 10 reconfigurators

        // Run acquire handle and reconfigure proxy concurrently
        var tasks = new List<Task>();

        // Tasks that acquire handles
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();
                    var handle = service.AcquireHandle();
                    lock (lockObj)
                    {
                        handles.Add(handle);
                    }
                    // Simulate some work
                    Thread.Sleep(10);
                    handle.Manager.Services.Should().NotBeEmpty();
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        // Tasks that reconfigure proxy
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    startBarrier.SignalAndWait();
                    service.ReconfigureProxy();
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("concurrent handle acquisition and proxy reconfiguration should not throw");

        // Dispose all acquired handles
        foreach (var handle in handles)
        {
            handle.Dispose();
        }

        // Service should still work after all the concurrent operations
        service.Manager.Should().NotBeNull();
        service.Manager.Services.Should().NotBeEmpty();
    }
}
