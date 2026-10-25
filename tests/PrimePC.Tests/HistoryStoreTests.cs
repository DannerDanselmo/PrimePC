using PrimePC.Testing;

namespace PrimePC.Tests;

public class HistoryStoreTests
{
    public sealed record Item(int Id, string Name);

    [Fact]
    public void AddKeepsTheNewestAndSaveReplacesEverything()
    {
        string dir = Directory.CreateTempSubdirectory("primepc-hist-").FullName;
        try
        {
            var store = new JsonHistoryStore<Item>(Path.Combine(dir, "sub", "h.json"), maxItems: 3);
            Assert.Empty(store.Load());
            for (int i = 1; i <= 5; i++)
                store.Add(new Item(i, $"item {i}"));
            Assert.Equal(new[] { 3, 4, 5 }, store.Load().Select(x => x.Id));

            // Apagar um item (como na lista de benchmarks e de sessões de jogo).
            store.Save(store.Load().Where(x => x.Id != 4));
            Assert.Equal(new[] { 3, 5 }, store.Load().Select(x => x.Id));
            Assert.False(File.Exists(Path.Combine(dir, "sub", "h.json.tmp")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
