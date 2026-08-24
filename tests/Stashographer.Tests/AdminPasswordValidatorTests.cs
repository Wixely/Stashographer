using Stashographer.Services.Security;

namespace Stashographer.Tests;

[Collection(AdminEnvironmentCollection.Name)]
public sealed class AdminPasswordValidatorTests : IDisposable
{
    public AdminPasswordValidatorTests() => SetPassword(null);

    public void Dispose() => SetPassword(null);

    [Fact]
    public void Direct_environment_value_is_accepted()
    {
        SetPassword("simple");

        var validator = new AdminPasswordValidator();

        Assert.True(validator.IsValid("simple"));
        Assert.False(validator.IsValid("different"));
    }

    [Fact]
    public void Password_change_invalidates_principal_from_previous_process_configuration()
    {
        SetPassword("first-password");
        var original = new AdminPasswordValidator();
        var principal = original.CreatePrincipal();
        Assert.True(original.IsCurrent(principal));

        SetPassword("second-password");
        var restarted = new AdminPasswordValidator();

        Assert.False(restarted.IsCurrent(principal));
    }

    [Fact]
    public void Missing_password_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new AdminPasswordValidator());
        Assert.Contains(AdminPasswordValidator.PasswordEnvironmentVariable, error.Message);
    }

    private static void SetPassword(string? value) =>
        Environment.SetEnvironmentVariable(AdminPasswordValidator.PasswordEnvironmentVariable, value);
}
