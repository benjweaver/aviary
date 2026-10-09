using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Aviary.App;

// Lets files and folders from Explorer be dropped onto a machine; send copies them into the guest and returns
// where they went. report shows progress and results.
static class FileDrop
{
    public static void Attach(UIElement target, Func<IReadOnlyList<string>, Task<string>> send, Action<string, bool> report)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Copy to the guest's Downloads";
            e.Handled = true;
        };
        target.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.Handled = true;
            var deferral = e.GetDeferral();
            IReadOnlyList<IStorageItem> items;
            try { items = await e.DataView.GetStorageItemsAsync(); }
            finally { deferral.Complete(); }
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count == 0) { report("Those items aren't files on this PC, so they can't be copied.", true); return; }
            report($"Copying {Describe(paths)} to the guest…", false);
            try { report($"Copied {Describe(paths)} to {await send(paths)} in the guest.", false); }
            catch (Exception ex) { report(ex.Message, true); }
        };
    }

    static string Describe(IReadOnlyList<string> paths) => paths.Count == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0])) : $"{paths.Count} items";
}
