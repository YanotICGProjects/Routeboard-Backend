using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Infrastructure.Storage
{
    public class AttachmentStorageOptions
    {
        /// <summary>
        /// appsettings:
        /// Attachments:RootPath
        /// </summary>
        public string RootPath { get; set; }
            = "App_Data/attachments";
    }
}
