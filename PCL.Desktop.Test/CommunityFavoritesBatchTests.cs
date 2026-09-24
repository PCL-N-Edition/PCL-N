using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Desktop.Features.Community;

namespace PCL.Desktop.Test;

[TestClass]
public sealed class CommunityFavoritesBatchTests
{
    [TestMethod]
    public void AddRange_DeduplicatesWithoutRemovingExistingFavorites()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcl-favorites-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            CommunityFavoritesStore store = new(Path.Combine(root, "favorites.json"));
            CommunityResourceEntry first = new("first", "first", "First", "", "mod", null, 0, null);
            CommunityResourceEntry second = new("second", "second", "Second", "", "mod", null, 0, null);
            CommunityFavoriteFolder folder = store.CreateFolder("Target");
            store.Toggle(first, CommunityResourceCategory.Mod, folder.Id);
            int changes = 0;
            store.Changed += (_, _) => changes++;
            Assert.AreEqual(1, store.AddRange([first, second, second], CommunityResourceCategory.Mod, folder.Id));
            Assert.AreEqual(2, store.Folders.Single(item => item.Id == folder.Id).Items.Count);
            Assert.AreEqual(0, store.Folders.Single(item => item.Id == CommunityFavoritesStore.DefaultFolderId).Items.Count);
            Assert.AreEqual(1, changes);
            Assert.AreEqual(0, store.AddRange([first, second], CommunityResourceCategory.Mod, folder.Id));
            Assert.AreEqual(1, changes);
            Assert.IsTrue(File.Exists(Path.Combine(root, "favorites.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
