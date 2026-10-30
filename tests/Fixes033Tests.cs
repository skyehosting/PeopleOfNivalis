using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NivalisAIChat.Ai;

namespace NivalisAIChat.Tests;

/// <summary>0.3.3: the fixes from the author's test of 0.3.2, and the features after it.</summary>
internal static partial class Program
{
    private static async Task Fixes033Tests()
    {
        string dir = TestRoot.NewFolder("fixes033");
        Directory.CreateDirectory(dir);
        HoldRuleTests();
        await ServiceTalkTests(dir);
        DoingsTests();
        await CircleTests(dir);
    }

    /// <summary>0.3.3: who may be held (never at work or mid-visit), and which customers can join a table's talk.</summary>
    private static void HoldRuleTests()
    {
        Console.WriteLine("-- 0.3.3: who may be held for a call or a conversation");
        Check(Social.ChatterPlan.MayHold(false, "") && Social.ChatterPlan.MayHold(false, null!), "someone free in the street may be held");
        Check(!Social.ChatterPlan.MayHold(true, ""), "never someone at work (a waiter, a cook): holding them stops their work");
        Check(new[] { "ComingToVenue", "WaitingToOrder", "WaitingForOrder", "Eating", "Complaining", "Leaving" }.All(step => !Social.ChatterPlan.MayHold(false, step)),
              "never a customer in the middle of a visit: holding them stalls ordering, being served and leaving");
        Check(Social.ChatterPlan.TableStep("") && Social.ChatterPlan.TableStep("WaitingToOrder") && Social.ChatterPlan.TableStep("WaitingForOrder") && Social.ChatterPlan.TableStep("Eating"),
              "at a table: someone on a bench, waiting to order, waiting for food or eating can join the table's talk (held lightly)");
        Check(!Social.ChatterPlan.TableStep("ComingToVenue") && !Social.ChatterPlan.TableStep("Complaining") && !Social.ChatterPlan.TableStep("Leaving"),
              "...not someone arriving, complaining or leaving");
        Check(Social.VenueRoles.Classify("AIAction_WorkplaceIdle", "IdleCrossedArms", false, false) == Social.VenueRoles.Role.StaffFree,
              "the game's own idle-at-work task (AIAction_WorkplaceIdle, as the log shows) is staff with a moment free");
    }
}
