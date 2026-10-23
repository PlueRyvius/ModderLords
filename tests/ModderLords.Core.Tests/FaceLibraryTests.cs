using ModderLords.Coop.Admin;

namespace ModderLords.Core.Tests;

public sealed class FaceLibraryTests : IDisposable
{
    private const string Dna = "<BodyProperties version=\"4\" age=\"25\" weight=\"0.5\" build=\"0.5\" key=\"00AA\" />";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ml-faces-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private FaceLibrary Library() => new(Path.Combine(_dir, "faces.json"));

    [Fact]
    public void A_missing_file_is_an_empty_library()
    {
        Assert.Empty(Library().Load());
    }

    [Fact]
    public void Saving_under_a_taken_name_replaces_it_and_the_list_is_sorted()
    {
        var lib = Library();
        lib.Save("Viking jarl", Dna, female: false);
        lib.Save("aldric", Dna, female: false);
        var faces = lib.Save("VIKING JARL", Dna.Replace("00AA", "00BB"), female: true);

        Assert.Equal(["aldric", "VIKING JARL"], faces.Select(f => f.Name));
        Assert.Contains("00BB", faces[1].Body);
        Assert.True(faces[1].Female);
        // Read back by a second instance, as another editor window would.
        Assert.Equal(2, Library().Load().Count);
    }

    [Fact]
    public void Delete_removes_by_name_ignoring_case()
    {
        var lib = Library();
        lib.Save("Mira", Dna, female: true);
        Assert.True(lib.Contains(" mira "));
        Assert.Empty(lib.Delete("MIRA"));
    }

    [Fact]
    public void A_face_without_a_name_or_real_dna_is_refused()
    {
        Assert.NotNull(FaceLibrary.Problem("", Dna));
        Assert.NotNull(FaceLibrary.Problem("x", "not dna"));
        Assert.NotNull(FaceLibrary.Problem(new string('a', FaceLibrary.MaxNameLength + 1), Dna));
        Assert.Throws<ArgumentException>(() => Library().Save("", Dna, false));
        Assert.Null(FaceLibrary.Problem("Fine", Dna));
    }

    [Fact]
    public void A_damaged_file_throws_rather_than_being_overwritten()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "faces.json"), "{ not json");
        Assert.ThrowsAny<Exception>(() => Library().Load());
        Assert.ThrowsAny<Exception>(() => Library().Save("x", Dna, false));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(_dir, "faces.json")));
    }
}
