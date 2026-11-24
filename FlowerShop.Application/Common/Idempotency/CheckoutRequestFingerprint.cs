using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using FlowerShop.Application.Common.Validation;
using FlowerShop.Application.DTOs.Orders;

namespace FlowerShop.Application.Common.Idempotency;

public static class CheckoutRequestFingerprint
{
    public static string Compute(CheckoutRequestDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        // Validate raw ASCII before uppercase conversion, including direct callers.
        var error = VoucherInputPolicy.ValidateCode(dto.VoucherCode,
            nameof(CheckoutRequestDto.VoucherCode), required: false).FirstOrDefault();
        if (error is not null)
            throw new ValidationException(error, null, dto.VoucherCode);

        var code = VoucherInputPolicy.NormalizeCode(dto.VoucherCode);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            // Preserve the exact v1 byte format when no voucher is requested.
            writer.Write(code is null ? (byte)1 : (byte)2);
            writer.Write(dto.CheckoutRequestId.ToByteArray());
            writer.Write(dto.ShippingAddressId.ToByteArray());
            writer.Write((int)dto.PaymentMethod);

            // Null, empty, whitespace and case differences remain significant for Note.
            writer.Write(dto.Note is not null);
            if (dto.Note is not null)
                writer.Write(dto.Note);

            // v2 appends the canonical code using BinaryWriter's length-prefixed UTF-8 format.
            if (code is not null)
                writer.Write(code);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}
