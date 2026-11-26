# Voucher API contract — COD MVP

All routes require a valid Bearer JWT. Customer identity is taken from `sub`, never a request body field. Admin and Customer routes require the corresponding role.

| Method | Route | Role | Success |
|---|---|---|---|
| GET | `/api/admin/vouchers` | Admin | 200 paged |
| GET | `/api/admin/vouchers/{voucherId}` | Admin | 200 detail |
| POST | `/api/admin/vouchers` | Admin | 201 with Location |
| PUT | `/api/admin/vouchers/{voucherId}` | Admin | 200 |
| PATCH | `/api/admin/vouchers/{voucherId}/status` | Admin | 200 |
| GET | `/api/vouchers/available` | Customer | 200 paged |
| POST | `/api/vouchers/preview` | Customer | 200 quote |
| POST | `/api/orders/checkout` | Customer | 201 new / 200 replay |
| POST | `/api/orders/{orderId}/cancel` | Customer | 200 |
| POST | `/api/admin/orders/{orderId}/cancel` | Admin | 200 |

Admin list: `codeKeyword`, `discountType`, `status`, `sort`, `page` (default 1), `pageSize` (default 10, max 50). Customer list: `page`, `pageSize` only. Customer DTO excludes global usage counter and internal ID. Admin create: `code`, `description`, `discountType` (1 percentage / 2 fixed), `discountValue`, `minOrderAmount`, `maxDiscountAmount` (percentage only), `usageLimit`, `perUserLimit`, `startAt`, `endAt` (explicit UTC offset), `isActive`. Update uses terms only, no code/status/counter. PATCH status: `{ "isActive": false }`. Code is trimmed uppercase ASCII `[A-Z0-9_-]{1,50}`, immutable. After any order history, commercial terms (type, value, minimum, cap, start) are frozen. Limits and end date may be edited subject to validation.

Preview request: `{ "voucherCode": "FLOWER20" }`. Response: `voucherCode`, `subtotal`, `shippingFee`, `discountAmount`, `totalAmount`, `quotedAtUtc`. Preview does not reserve usage; available list is not an eligibility guarantee. Revalidate at checkout.

Checkout example (illustrative GUIDs):

```json
{
  "checkoutRequestId": "00112233-4455-6677-8899-aabbccddeeff",
  "shippingAddressId": "10213243-5465-7687-98a9-bacbdcedfe0f",
  "paymentMethod": 1,
  "voucherCode": "FLOWER20",
  "note": null
}
```

Omit voucherCode or use null/blank for no discount. Pricing is calculated by the server from cart/product/voucher data; COD shipping fee currently zero. Response: `orderId`, `orderCode`, `status`, `paymentMethod`, `paymentStatus`, `subtotal`, `shippingFee`, `discountAmount`, `totalAmount`, `items`, `createdAt`, `isReplay`. New returns 201, replay returns 200 using persisted monetary snapshots and current serialized order/payment states. No repeated redemption. Customer cancellation only Pending; Admin Pending/Confirmed; unpaid COD required. Both cancel routes take no body. Response includes `orderId`, `orderCode`, `previousStatus`, `status`, `updatedAtUtc`. Cancelled order preserves voucher ID and discount; cart is not replenished automatically.

Business error responses use ProblemDetails `detail`. Validation errors from MVC use ValidationProblemDetails `errors`. 401 unauthenticated, 403 wrong role, 404 voucher/order/address not found (including other customer's order/address), 409 business conflicts. Examples: `VOUCHER_INACTIVE`, `VOUCHER_NOT_STARTED`, `VOUCHER_EXPIRED`, `VOUCHER_MIN_ORDER_NOT_MET`, `VOUCHER_USAGE_LIMIT_REACHED`, `VOUCHER_PER_USER_LIMIT_REACHED`, `VOUCHER_DISCOUNT_ZERO`, `CHECKOUT_TOTAL_OUT_OF_RANGE`, `CHECKOUT_REQUEST_MISMATCH`, `CHECKOUT_REQUEST_ID_IN_USE`, `CHECKOUT_VOUCHER_NOT_ENABLED`, `ORDER_ALREADY_CANCELLED`, `INVALID_ORDER_STATUS_TRANSITION`, `VOUCHER_USAGE_INCONSISTENT`, `STOCK_RESTORATION_FAILED`. Do not silently retry a new request ID after an uncertain network outcome. Refresh cart and quote after eligibility conflicts. Do not automatically repair data consistency errors.
