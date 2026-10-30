using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NivalisAIChat.Ai;
using NivalisAIChat.Game;
using NivalisAIChat.Social;

namespace NivalisAIChat.Npc;

/// <summary>
/// FB-34: staff taking a customer's order at a food place, in their own words. One small request writes every turn the
/// exchange can take; the runtime plays the ones that happen. Only for a customer who already has a record (the staff
/// member is created if need be), never anyone under 18; nothing is kept after a reset of either.
/// </summary>
public sealed partial class ChatterBrain
{
    /// <summary>The order exchange as the AI wrote it, with both people's records. Null if it can't or shouldn't be written.</summary>
    /// <param name="epochs">Both people's epochs from when the exchange began (taken on the game's thread before anything is read).</param>
    public async Task<(VenuePlan.Lines lines, NpcRecord staff, NpcRecord customer)?> OrderTalkAsync(
        string staffId, NpcVisual staffVisual, GameSnapshot staffSnap, string customerId, NpcVisual customerVisual, GameSnapshot customerSnap,
        string place, bool atTable, IReadOnlyDictionary<string, long> epochs, CancellationToken ct, bool justSitting = false)
    {
        bool Current(string id) => epochs.TryGetValue(id, out var e) && _brain.EpochFor(id) == e;
        if (staffId.Length == 0 || customerId.Length == 0 || staffId == customerId) return null;
        if (_brain.KnownMinors.Contains(staffId) || _brain.KnownMinors.Contains(customerId)) return null;
        // The customer already has a record (checked by the caller): this only reads it. The staff member may be new.
        var customer = await _brain.EnsurePersonAsync(customerId, customerVisual, customerSnap, ct).ConfigureAwait(false);
        if (customer == null || _brain.IsKnownMinor(customer) || ct.IsCancellationRequested) return null;
        var staff = await _brain.EnsurePersonAsync(staffId, staffVisual, staffSnap, ct).ConfigureAwait(false);
        if (staff == null || _brain.IsKnownMinor(staff) || ct.IsCancellationRequested) return null;
        if (!Current(staffId) || !Current(customerId)) return null; // reset meanwhile
        var req = ServicePrompts.OrderRequest(new ChatterPrompts.Person { Record = staff, Snap = staffSnap },
                                              new ChatterPrompts.Person { Record = customer, Snap = customerSnap, Seated = atTable },
                                              _pairs.Get(staffId, customerId), place, atTable, justSitting);
        var res = await _llm.CompleteAsync(req, ct).ConfigureAwait(false);
        if (!res.Ok) { _problem("Order talk couldn't be written: " + res.Error); return null; }
        if (ct.IsCancellationRequested || !Current(staffId) || !Current(customerId)) return null;
        var lines = ServicePrompts.ParseOrder(res.Text, staff, customer, justSitting);
        if (lines == null) { _log("Order talk came back unusable; the written lines play."); return null; }
        Voice.ToneState Voiced(NpcRecord rec, string word)
        {
            var t = Voice.Tone.Start(Features.Mood(rec.State.Mood), rec.State.MoodIntensity);
            Voice.Tone.Feel(t, word.Length > 0 ? word : t.Lead, "moderate");
            return t;
        }
        lines.StaffVoice = Voiced(staff, lines.StaffTone);
        lines.CustomerVoice = Voiced(customer, lines.CustomerTone);
        _log($"Order talk at {(place.Length > 0 ? place : "a food place")} ({staff.Persona.Name}, {customer.Persona.Name}): written by the AI.");
        return (lines, staff, customer);
    }

    /// <summary>
    /// What stays with them after an order exchange: both keep it as a short "service" talk (its own small cap, so it never
    /// pushes out conversations); a customer who walked off before ordering stays with the staff member as a line in their
    /// day. Names are used only when they know each other (a pair already exists); nothing is added to how they get on.
    /// </summary>
    /// <param name="said">What was said, in order, by whom (true = the staff member).</param>
    public Task RememberOrderTalkAsync(string staffId, string staffName, string customerId, string customerName, IReadOnlyDictionary<string, long> epochs,
        GameSnapshot place, string venue, IReadOnlyList<(bool staff, string text)> said, string dishes, bool walkedOff)
    {
        if (said.Count == 0) return Task.CompletedTask;
        bool known = _pairs.Get(staffId, customerId) != null;
        string staffCalled = known && staffName.Length > 0 ? Prompts.FirstName(staffName) : "the staff";
        string customerCalled = known && customerName.Length > 0 ? Prompts.FirstName(customerName) : "a customer";
        string at = venue.Length > 0 ? " at " + venue : "";
        // Each keeps the lines with their own name and the other's only if they know each other (review R4).
        List<string> LinesFor(bool staffKeeps)
        {
            string staffLabel = staffName.Length > 0 && (staffKeeps || known) ? Prompts.FirstName(staffName) : "Staff";
            string customerLabel = customerName.Length > 0 && (!staffKeeps || known) ? Prompts.FirstName(customerName) : "Customer";
            return said.Select(l => $"{(l.staff ? staffLabel : customerLabel)}: {l.text}").ToList();
        }
        string staffSummary = walkedOff ? $"{Cap(customerCalled)} got up and left before ordering{at}." : $"Took {customerCalled}'s order{at}{(dishes.Length > 0 ? ": " + dishes : "")}.";
        string customerSummary = $"Ordered{(dishes.Length > 0 ? " " + dishes : "")}{at}{(known ? ", from " + staffCalled : "")}.";

        Task Keep(string id, string with, string summary, string? lifeLine, List<string> lines)
        {
            if (!epochs.TryGetValue(id, out var epoch)) return Task.CompletedTask;
            return _brain.Mutate(id, rec =>
            {
                if (_brain.EpochFor(id) != epoch || _brain.IsKnownMinor(rec)) return false;
                rec.AddTalk(new Talk { GameDay = place.GameDay, Clock = place.Clock, Where = venue.Length > 0 ? venue : place.Location, Kind = Talk.Service, With = with, Lines = lines, Summary = summary });
                if (lifeLine != null)
                {
                    rec.LifeLog.Add(new LifeEvent { GameDay = place.GameDay, Utc = DateTime.UtcNow, Text = ReplyParser.Quotable(lifeLine, 600) });
                    if (rec.LifeLog.Count > 40) rec.LifeLog.RemoveRange(0, rec.LifeLog.Count - 40);
                }
                return true;
            });
        }

        var jobs = new List<Task> { Keep(staffId, known ? Prompts.FirstName(customerName) : "a customer", staffSummary, walkedOff ? staffSummary : null, LinesFor(true)) };
        if (!walkedOff) jobs.Add(Keep(customerId, known ? Prompts.FirstName(staffName) : "the staff", customerSummary, null, LinesFor(false)));
        return Task.WhenAll(jobs);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
