using Easydict.WinUI.Views.Controls;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for ProtectedCursorHelper.
/// The helper uses cached reflection against the protected <c>UIElement.ProtectedCursor</c>
/// property, which is the reason the class exists (<c>Border</c> is sealed and the property
/// is not public). This test asserts the reflection lookup succeeds on the current SDK so a
/// future SDK bump that renames or removes the property fails at test time instead of at
/// runtime in user-facing UI.
/// </summary>
[Trait("Category", "WinUI")]
public class ProtectedCursorHelperTests
{
    [Fact]
    public void StaticInitializer_ResolvesProtectedCursorProperty()
    {
        // Touching any member of the static class forces the static field initializer to run.
        // If UIElement.ProtectedCursor is gone or renamed, the `?? throw` in the field
        // initializer surfaces here as a TypeInitializationException.
        var action = () => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(ProtectedCursorHelper).TypeHandle);

        action.Should().NotThrow(
            "UIElement.ProtectedCursor must remain reachable via reflection; " +
            "a failure here means the WindowsAppSDK API shape has changed and " +
            "the cursor-on-Border workaround needs updating.");
    }
}
