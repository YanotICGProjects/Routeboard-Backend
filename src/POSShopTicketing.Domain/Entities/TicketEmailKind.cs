using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Domain.Entities
{
    public enum TicketEmailKind
    {
        /// <summary>Automatic no-reply message about a ticket.</summary>
        Notice,

        /// <summary>An agent's message to the customer, inside the ticket's mail thread.</summary>
        Conversation
    }
}
