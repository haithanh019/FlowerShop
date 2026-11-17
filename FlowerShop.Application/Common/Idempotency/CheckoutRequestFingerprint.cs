using System.Security.Cryptography;
using System.Text;
using FlowerShop.Application.DTOs.Orders;

namespace FlowerShop.Application.Common.Idempotency;

public static class CheckoutRequestFingerprint
{
    public static string Compute(CheckoutRequestDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            // Format version, to avoid silently changing hash semantics later.
            writer.Write((byte)1);
            writer.Write(dto.CheckoutRequestId.ToByteArray());
            writer.Write(dto.ShippingAddressId.ToByteArray());
            writer.Write((int)dto.PaymentMethod);

            // Null and empty notes are intentionally different payloads.
            writer.Write(dto.Note is not null);
            if (dto.Note is not null)
                writer.Write(dto.Note);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}
