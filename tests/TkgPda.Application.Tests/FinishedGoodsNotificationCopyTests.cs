using TkgPda.Application;
using Xunit;

namespace TkgPda.Application.Tests;

public sealed class FinishedGoodsNotificationCopyTests
{
    [Fact]
    public void Delivery_copy_uses_the_location_name_and_request_number()
    {
        var title = FinishedGoodsNotificationCopy.Title("Alan 4");
        var body = FinishedGoodsNotificationCopy.Body("ABC-123", "SH-01-A", 145);
        var unitId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Assert.Equal("Kasa Koyuldu — Alan 4", title);
        Assert.Equal("ABC-123 · SH-01-A · Talep #145", body);
        Assert.Equal($"finished-goods-delivery:{unitId:D}", FinishedGoodsNotificationCopy.EventKey(unitId));
        Assert.DoesNotContain(unitId.ToString(), title);
        Assert.DoesNotContain(unitId.ToString(), body);
    }
}
