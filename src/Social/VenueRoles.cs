using System;
using System.Collections.Generic;

namespace NivalisAIChat.Social;

/// <summary>
/// Who's who at a food place, from what the game has them doing (FB-21). The game's own task tells staff from customers:
/// taking an order at a table is a WORKER's task (`TakeOrderAgentActionType`, a `VenueWorkerAgentActionType`), and its
/// "TalkOrder" pose is the waiter's; at a counter the customer orders with the same pose. Unity-free, so it's tested.
/// </summary>
public static class VenueRoles
{
    private static readonly string[] WorkWords = { "order", "cook", "prep", "serv", "clean", "wiping", "polish", "chopping", "sweep", "shift", "behind the bar",
                                                   "waiting tables", "trash", "dishes", "storeroom", "hauling", "running", "complaint", "minding the counter", "keeping watch", "delivery" };

    /// <summary>
    /// An activity, in words (GameContext's description of what they're doing), that's work: the words the mod's
    /// "busy working" check always used. CF-11: also what "at work" means for the pad's interact button. No Unity.
    /// </summary>
    public static bool IsWorkText(string? activity)
    {
        string a = (activity ?? "").ToLowerInvariant();
        return a.Length > 0 && Array.Exists(WorkWords, w => a.Contains(w));
    }
    public enum Role
    {
        Other,
        /// <summary>Staff taking an order (the waiter at a table, or staff at a counter).</summary>
        StaffTakingOrder,
        /// <summary>Staff bringing someone their meal.</summary>
        StaffServing,
        /// <summary>Staff with a moment free (behind the counter, idle at work): the ones who chat.</summary>
        StaffFree,
        /// <summary>Staff cooking, cleaning, carrying: busy.</summary>
        StaffBusy,
        /// <summary>A customer ordering at a counter.</summary>
        CustomerOrdering,
        /// <summary>A customer at the place (seated waiting, or standing in line), not eating yet.</summary>
        CustomerWaiting,
        /// <summary>A customer eating or drinking, seated.</summary>
        CustomerEating,
    }

    /// <summary>Body actions only staff play (counters, bars, huts, kitchens).</summary>
    public static readonly HashSet<string> StaffActions = new(StringComparer.Ordinal)
    {
        "TalkCustomer", "IdleStaff", "BarIdle", "Shop", "HutIdle", "CleanBar", "CleanGlasses", "CleanTable", "CutFood",
        "CookerLoop", "FryerLoop", "PanLoop", "OvenLoop", "BlenderLoop",
    };

    /// <summary>Staff body actions with a moment free (not cooking or cleaning).</summary>
    public static readonly HashSet<string> FreeStaffActions = new(StringComparer.Ordinal) { "IdleStaff", "BarIdle", "HutIdle", "Shop", "TalkCustomer" };

    /// <summary>
    /// The game's worker tasks by name, for when the task's type can't be read (the names the game gives them, as the log
    /// shows: "AIAction_TakeOrder", "AIAction_CleanupMeal"...).
    /// </summary>
    public static readonly string[] WorkerTasks =
    {
        "AIAction_TakeOrder", "AIAction_ServeMeal", "AIAction_IdleAtWorkplace", "AIAction_WorkplaceIdle", "AIAction_MakeMeal", "AIAction_CleanupMeal",
        "AIAction_CleanupStorage", "AIAction_CleanupTrash", "AIAction_DeliverIngredients", "AIAction_ProcessIngredient",
        "AIAction_Manage", "AIAction_ManageComplaint",
    };

    public static bool IsWorkerTaskName(string ghostTask) =>
        !string.IsNullOrEmpty(ghostTask) && Array.Exists(WorkerTasks, w => ghostTask.StartsWith(w, StringComparison.Ordinal));

    /// <summary>
    /// Their role. <paramref name="ghostTask"/>: the game's task name ("AIAction_TakeOrder"); <paramref name="action"/>: the
    /// body action ("TalkOrder", "Eat"); <paramref name="workerTask"/>: the task is a worker's (read from its type);
    /// <paramref name="seated"/>: the game says they're sitting.
    /// </summary>
    public static Role Classify(string ghostTask, string action, bool workerTask, bool seated)
    {
        ghostTask ??= "";
        action ??= "";
        bool staff = workerTask || IsWorkerTaskName(ghostTask) || StaffActions.Contains(action);
        if (staff)
        {
            if (action == "TalkOrder" || action == "TalkCustomer" && ghostTask.StartsWith("AIAction_TakeOrder", StringComparison.Ordinal)) return Role.StaffTakingOrder;
            if (ghostTask.StartsWith("AIAction_ServeMeal", StringComparison.Ordinal)) return Role.StaffServing;
            if (FreeStaffActions.Contains(action) || ghostTask.StartsWith("AIAction_IdleAtWorkplace", StringComparison.Ordinal)
                || ghostTask.StartsWith("AIAction_WorkplaceIdle", StringComparison.Ordinal)) return Role.StaffFree; // the game's name for it, as the log shows
            return Role.StaffBusy;
        }
        if (action == "TalkOrder") return Role.CustomerOrdering;
        if (seated && action is "Eat" or "Drink") return Role.CustomerEating;
        if (ghostTask.StartsWith("AIAction_VisitVenue", StringComparison.Ordinal) || ghostTask.StartsWith("AIAction_VisitVending", StringComparison.Ordinal))
            return Role.CustomerWaiting;
        return Role.Other;
    }

    public static bool IsStaff(Role r) => r is Role.StaffTakingOrder or Role.StaffServing or Role.StaffFree or Role.StaffBusy;
    public static bool IsCustomer(Role r) => r is Role.CustomerOrdering or Role.CustomerWaiting or Role.CustomerEating;

    /// <summary>Short words for /nearby.</summary>
    public static string Describe(Role r) => r switch
    {
        Role.StaffTakingOrder => "staff, taking an order",
        Role.StaffServing => "staff, serving",
        Role.StaffFree => "staff, free",
        Role.StaffBusy => "staff, busy",
        Role.CustomerOrdering => "customer, ordering",
        Role.CustomerWaiting => "customer, waiting",
        Role.CustomerEating => "customer, eating",
        _ => "-",
    };

    private static readonly string[] Serves = { "Here's your {0}.", "One {0}. Enjoy.", "Here you go: the {0}.", "{0} for you." };
    private static readonly string[] PlainServes = { "Here you go. Enjoy.", "Here you are.", "There you go." };
    private static readonly string[] ServeThanks = { "Thanks.", "Thank you.", "Great, thanks.", "Smells good." };

    /// <summary>Staff bringing a meal, and the customer's answer (written lines, no AI): the dish by the game's name when known.</summary>
    public static (string staff, string customer) ServeLines(string dish, Random rng)
    {
        string staff = string.IsNullOrWhiteSpace(dish) ? PlainServes[rng.Next(PlainServes.Length)] : string.Format(Serves[rng.Next(Serves.Length)], dish.Trim());
        return (staff, ServeThanks[rng.Next(ServeThanks.Length)]);
    }
}
