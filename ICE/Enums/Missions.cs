namespace ICE.Enums
{
    [Flags]
    public enum MissionAttributes
    {
        None = 0,                  // Impossible to have no attributes. If None - we failed to parse the mission.

        // --- Activity Type ---
        Craft = 1,                 //
        Gather = 2,                //
        Fish = 4,                  //

        // --- Constraints ---
        Limited = 8,               // Supplies/Nodes/Bait

        // --- Item/Focus Type ---
        Collectables = 16,         // Collectables
        ReducedItems = 32,         // Reduction
        ExpertCraft = 64,          // Expert Craft

        // --- Scoring Method ---
        ScoreTimeRemaining = 128,  // Time Attack (Speeeeeeed)
        ScoreChains = 256,         // Chain Bonus
        ScoreGatherersBoon = 512,  // Gatherers Boon
        ScoreLargestSize = 1024,   // Largest fish caught
        ScoreVariety = 2048,       // Fish Variety
        ScoreScore = 4096,         // Minimum Score Requirement

        // --- Misc ---
        Critical = 8192,                     // Critical Mission
        ProvisionalTimed = 16384,            // Timed Mission
        ProvisionalWeather = 32768,          // Weather Mission
        ProvisionalSequential = 65536,       // Sequential Mission

        GreaterReachGather = 131072, // Greater Reach + Gather 100 Items
        GreaterReachChain = 262144,   // Greater Reach + Chain Scoring
        GreaterReachBoon = 524288,   // Greater Reach + Boon Scoring

        Mastership = 1048576,        // マスターシップミッション (WKSMissionUnit.Unknown2==true, LevelGroup6, 各職3つ「MASTER：」)
    }
}