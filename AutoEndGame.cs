using System;
using System.Collections.Generic;
using System.Linq;

namespace ChestCounter;

/// <summary>
/// Decides when a match has ended, from Fortnite-API's season match count.
/// Fortnite-API updates a few minutes after a match, so the game is split by the time
/// the stats changed (the match end), not by when we noticed: chests counted after that
/// time already belong to the next game.
/// </summary>
public sealed class AutoEndGame
{
    static readonly TimeSpan ManualEndMemory = TimeSpan.FromMinutes(15);

    int? baseline;
    DateTime? lastPollUtc;
    readonly List<DateTime> manualEnds = new();

    /// <summary>Forget the match count, e.g. when switched on or Fortnite was closed for a while.</summary>
    public void Reset()
    {
        baseline = null;
        lastPollUtc = null;
        manualEnds.Clear();
    }

    /// <summary>The player pressed End game themselves; the next new match is already saved.</summary>
    public void NoteManualEnd(DateTime nowUtc) => manualEnds.Add(nowUtc);

    /// <summary>Manual End game presses the API hasn't caught up with yet.</summary>
    public int PendingManualEnds(DateTime nowUtc)
    {
        manualEnds.RemoveAll(t => nowUtc - t > ManualEndMemory);
        return manualEnds.Count;
    }

    /// <summary>
    /// Feed one stats check. Returns the match end time (UTC) to save the current game at,
    /// or null if there's nothing new to save. The first check only sets the starting point.
    /// </summary>
    public DateTime? OnPoll(int matches, DateTime? lastModifiedUtc, DateTime nowUtc, out int newMatches)
    {
        newMatches = 0;
        DateTime? endAt = null;
        PendingManualEnds(nowUtc); // drops old manual presses (e.g. ones made in modes the API doesn't count)

        if (baseline is int before && matches > before)
        {
            int fresh = matches - before;
            int alreadySaved = Math.Min(manualEnds.Count, fresh);
            manualEnds.RemoveRange(0, alreadySaved);
            fresh -= alreadySaved;
            if (fresh > 0)
            {
                newMatches = fresh;
                // Without a match end time, the last check is the safest cut: everything before it was the old game.
                endAt = lastModifiedUtc ?? lastPollUtc ?? nowUtc;
            }
        }

        baseline = matches;
        lastPollUtc = nowUtc;
        return endAt;
    }

    /// <summary>Splits chest times into those up to the match end and those after it.</summary>
    public static (int Before, List<DateTime> After) Split(IReadOnlyList<DateTime> chestTimesUtc, DateTime endUtc) =>
        (chestTimesUtc.Count(t => t <= endUtc), chestTimesUtc.Where(t => t > endUtc).ToList());
}
