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
}
