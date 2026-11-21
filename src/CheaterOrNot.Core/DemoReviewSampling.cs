namespace CheaterOrNot.Core;

internal static class DemoReviewSampling
{
    public static IEnumerable<T> Across<T>(IReadOnlyList<T> rows, int limit)
    {
        var count = Math.Min(rows.Count, limit);
        return Enumerable.Range(0, count).Select(index =>
            rows[count == 1 ? 0 : (int)((long)index * (rows.Count - 1) / (count - 1))]);
    }

    public static T[] Balanced<T>(IReadOnlyList<T> rows, int limit, Func<T, string> player, Func<T, int> round)
    {
        limit = Math.Min(limit, rows.Count);
        if (limit == 0) return [];
        var players = rows.Select((row, index) => (row, index))
            .GroupBy(item => player(item.row), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.ToArray()).ToArray();
        var selectedPlayers = Across(players, Math.Min(players.Length, limit)).ToArray();
        var playerQuotas = Quotas(selectedPlayers.Select(group => group.Length).ToArray(), limit);
        var indices = new List<int>();
        for (var playerIndex = 0; playerIndex < selectedPlayers.Length; playerIndex++)
        {
            var rounds = selectedPlayers[playerIndex].GroupBy(item => round(item.row)).OrderBy(group => group.Key)
                .Select(group => group.ToArray()).ToArray();
            var selectedRounds = playerQuotas[playerIndex] == 1 && selectedPlayers.Length > 1 ?
                [rounds[(int)((long)playerIndex * (rounds.Length - 1) / (selectedPlayers.Length - 1))]] :
                Across(rounds, Math.Min(rounds.Length, playerQuotas[playerIndex])).ToArray();
            var roundQuotas = Quotas(selectedRounds.Select(group => group.Length).ToArray(), playerQuotas[playerIndex]);
            for (var roundIndex = 0; roundIndex < selectedRounds.Length; roundIndex++)
            {
                var group = selectedRounds[roundIndex];
                if (roundQuotas[roundIndex] == 1 && selectedRounds.Length > 1)
                    indices.Add(group[(int)((long)roundIndex * (group.Length - 1) / (selectedRounds.Length - 1))].index);
                else indices.AddRange(Across(group, roundQuotas[roundIndex]).Select(item => item.index));
            }
        }
        return indices.Order().Select(index => rows[index]).ToArray();
    }

    private static int[] Quotas(IReadOnlyList<int> capacities, int limit)
    {
        var quotas = new int[capacities.Count];
        var remaining = limit;
        while (remaining > 0)
            for (var index = 0; index < quotas.Length && remaining > 0; index++)
                if (quotas[index] < capacities[index])
                {
                    quotas[index]++;
                    remaining--;
                }
        return quotas;
    }
}
