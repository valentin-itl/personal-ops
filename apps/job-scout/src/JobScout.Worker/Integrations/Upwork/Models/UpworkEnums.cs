namespace JobScout.Worker.Integrations.Upwork.Models;

public enum UpworkJobType
{
    /// <summary>Missing, or a value the client does not know (see the response diagnostics).</summary>
    Unknown = 0,
    Fixed = 1,
    Hourly = 2,
}

public enum UpworkExperienceLevel
{
    /// <summary>Missing, or a value the client does not know (see the response diagnostics).</summary>
    Unknown = 0,
    EntryLevel = 1,
    Intermediate = 2,
    Expert = 3,
}

/// <summary>Hours per week.</summary>
public enum UpworkWorkload
{
    FullTime = 1,
    PartTime = 2,
    AsNeeded = 3,
}

/// <summary>Expected project length. Upwork applies it to hourly jobs only.</summary>
public enum UpworkProjectDuration
{
    LessThanOneMonth = 1,
    OneToThreeMonths = 2,
    ThreeToSixMonths = 3,
    MoreThanSixMonths = 4,
}

public enum UpworkJobSort
{
    /// <summary>Newest first.</summary>
    Recency = 1,

    /// <summary>Refused by Upwork together with title, skills, category or subcategory.</summary>
    Relevance = 2,
    ClientTotalCharge = 3,
    ClientRating = 4,
}

public enum UpworkSmartSearchMode
{
    /// <summary>Upwork's personalized recommendations ranked by fit. Ignores date filters.</summary>
    BestMatch = 1,

    /// <summary>Newest postings matched to the freelancer, newest first. Honours date filters.</summary>
    MostRecent = 2,
}
