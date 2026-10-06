using TkgPda.Application;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class FinishedGoodsPushCopyTests
{
    [Fact]
    public void Delivery_copy_uses_the_location_name_and_omits_identifiers()
    {
        var unitId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var title = FinishedGoodsPushCopy.Title("Alan 4");
        var body = FinishedGoodsPushCopy.Body("ABC-123", "SH-01-A", 145);

        Assert.Equal("Kasa Koyuldu — Alan 4", title);
        Assert.Equal("ABC-123 · SH-01-A · Talep #145", body);
        Assert.Equal($"finished-goods-delivery:{unitId:D}", FinishedGoodsPushCopy.EventKey(unitId));
        Assert.DoesNotContain(unitId.ToString(), title + body);
    }

    [Fact]
    public void Enabled_push_without_a_project_id_fails_validation()
    {
        new PushNotificationOptions().Validate();
        new PushNotificationOptions { Enabled = true, FirebaseProjectId = "tkg-example" }.Validate();

        var missingProject = new PushNotificationOptions { Enabled = true, FirebaseProjectId = "  " };
        var wrongProvider = new PushNotificationOptions
        {
            Enabled = true,
            Provider = "Other",
            FirebaseProjectId = "tkg-example"
        };
        Assert.Throws<InvalidOperationException>(missingProject.Validate);
        Assert.Throws<InvalidOperationException>(wrongProvider.Validate);
    }
}
