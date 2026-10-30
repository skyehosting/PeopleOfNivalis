namespace NivalisAIChat.Social;

/// <summary>
/// FB-73: what to do with someone who came fishing with you, each look (Unity-free, tested; review FB73-1/5/9). The hold's
/// own state decides, so a flag can never outlive the pose it stands for.
/// </summary>
public static class FishPlan
{
    public enum Step { Nothing, Pose, StandUp, BackToFollowing }

    /// <param name="fishingFlag">We think they're fishing beside you (Follower.Fishing).</param>
    /// <param name="seatBusy">Their hold is walking to, entering, in or leaving a pose.</param>
    /// <param name="seatFailed">The last pose asked for couldn't be reached.</param>
    /// <param name="distance">How far they are from you (flat metres).</param>
    /// <param name="mayPose">You're fishing, a pose exists, and they haven't just failed to get there.</param>
    public static Step Decide(bool fishingFlag, bool seatBusy, bool seatFailed, float distance, bool mayPose)
    {
        if (fishingFlag)
        {
            if (seatFailed && !seatBusy) return Step.BackToFollowing; // couldn't get there: they stay with you instead
            if (!seatBusy) return Step.BackToFollowing;                // the pose was lost (a chat mid-walk, a drop): follow again
            if (distance > 10f) return Step.StandUp;                   // you wandered off with your rod: they get up and come
            return Step.Nothing;
        }
        return mayPose && !seatBusy ? Step.Pose : Step.Nothing;
    }
}
