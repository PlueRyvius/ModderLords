using ModderLords.Core.Overlay;
using Xunit;

namespace ModderLords.Core.Tests;

/// <summary>
/// What happens to the overlay when the folders it points at are deleted underneath it — which is exactly what a
/// player does when they clear the Steam workshop folder to force a clean re-download. The shadow folders under
/// %LOCALAPPDATA% survive that wipe, so their junctions are left dangling and must still be repointable.
///
/// Windows keeps a dangling junction visible (Exists is true and the recorded target still reads back), so the
/// normal create/remove paths work on it. These tests pin that behaviour: if a future .NET or Windows change made
/// a dangling junction invisible, Create would try to make one over an existing entry and re-syncing would break
/// for every player who had cleared their workshop folder.
/// </summary>
public class DanglingJunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mc-dang-" + Guid.NewGuid().ToString("N"));

    public DanglingJunctionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Dir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void A_junction_whose_target_was_deleted_is_still_recognised_as_a_junction()
    {
        var target = Dir("target");
        var link = Path.Combine(_root, "link");
        Junction.Create(link, target);

        Directory.Delete(target, true);

        Assert.True(Junction.IsJunction(link));                       // not mistaken for a real directory
        Assert.Equal(target, Junction.Target(link), ignoreCase: true); // still says where it pointed
    }

    [Fact]
    public void A_dangling_junction_can_be_repointed_at_the_redownloaded_folder()
    {
        var oldTarget = Dir("before");
        var newTarget = Dir("after");
        var link = Path.Combine(_root, "link");
        Junction.Create(link, oldTarget);
        Directory.Delete(oldTarget, true);

        Junction.Create(link, newTarget);   // re-sync after the workshop item comes back

        Assert.Equal(newTarget, Junction.Target(link), ignoreCase: true);
    }

    [Fact]
    public void A_dangling_junction_can_be_removed_without_touching_anything_else()
    {
        var target = Dir("target");
        var link = Path.Combine(_root, "link");
        Junction.Create(link, target);
        Directory.Delete(target, true);

        Junction.Remove(link);

        Assert.False(Directory.Exists(link));
    }

    [Fact]
    public void Removing_a_junction_never_deletes_what_it_points_at()
    {
        // The whole safety story for the overlay: the link goes, the mod folder stays.
        var target = Dir("target");
        File.WriteAllText(Path.Combine(target, "SubModule.xml"), "<Module />");
        var link = Path.Combine(_root, "link");
        Junction.Create(link, target);

        Junction.Remove(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "SubModule.xml")));
    }
}
