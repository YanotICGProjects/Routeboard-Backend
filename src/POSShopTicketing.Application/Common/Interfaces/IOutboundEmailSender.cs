using POSShopTicketing.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Common.Interfaces
{
    public interface IOutboundEmailSender
    {
        /// <summary>Sends and THROWS on failure. Returns Resend's email id.</summary>
        Task<string?> SendAsync(OutboundEmail email, CancellationToken ct);
    }
}
