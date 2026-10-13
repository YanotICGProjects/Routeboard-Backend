using Microsoft.Extensions.Options;
using POSShopTicketing.Application.Common.Interfaces;

namespace POSShopTicketing.Infrastructure.Storage
{
    public class LocalDiskAttachmentStorage : IAttachmentStorage
    {
        private readonly string _root;

        public LocalDiskAttachmentStorage(
            IOptions<AttachmentStorageOptions> options)
        {
            _root = Path.GetFullPath(
                options.Value.RootPath);

            Directory.CreateDirectory(_root);
        }

        public async Task<string> SaveAsync(
            Guid tenantId,
            Guid ticketId,
            string fileName,
            byte[] content,
            CancellationToken ct)
        {
            var ext = Path.GetExtension(fileName);

            if (ext.Length > 10 ||
                ext.Any(c => !char.IsLetterOrDigit(c) && c != '.'))
            {
                ext = string.Empty;
            }

            var key =
                $"{tenantId:N}/{ticketId:N}/{Guid.NewGuid():N}{ext}";

            var path = ResolvePath(key);

            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);

            await File.WriteAllBytesAsync(
                path,
                content,
                ct);

            return key;
        }

        public Task<Stream> OpenReadAsync(
            string storageKey,
            CancellationToken ct)
        {
            return Task.FromResult<Stream>(
                new FileStream(
                    ResolvePath(storageKey),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read));
        }

        private string ResolvePath(string key)
        {
            var full = Path.GetFullPath(
                Path.Combine(_root, key));

            if (!full.StartsWith(
                _root,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Invalid storage key.");
            }

            return full;
        }
    }
}