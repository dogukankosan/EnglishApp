using EnglishApp.Shared.Enums;

namespace EnglishApp.Api.Services.Fsrs;

public sealed record FsrsCard(
    CardState State,
    double Stability,
    double Difficulty,
    int Step,
    int Reps,
    int Lapses,
    DateTime Due,
    DateTime? LastReview)
{
    public static FsrsCard CreateNew(DateTime now) => new(CardState.New, 0, 0, 0, 0, 0, now, null);
}

public sealed record FsrsReviewResult(FsrsCard Card, double ElapsedDays, double ScheduledDays);

/// <summary>
/// FSRS-5 aralıklı tekrar algoritması (open-spaced-repetition/py-fsrs mantığıyla).
/// Kartın "hafızadaki kalıcılığını" (Stability) ve "zorluğunu" (Difficulty) takip eder;
/// bir sonraki tekrarı, hatırlama ihtimali hedef orana (varsayılan %90) düştüğü ana planlar.
/// </summary>
public sealed class FsrsScheduler
{
    // FSRS-5 varsayılan parametreleri (milyonlarca tekrar verisiyle optimize edilmiş)
    public static readonly double[] DefaultWeights =
    [
        0.40255, 1.18385, 3.173, 15.69105, 7.1949, 0.5345, 1.4604, 0.0046, 1.54575, 0.1192,
        1.01925, 1.9395, 0.11, 0.29605, 2.2698, 0.2315, 2.9898, 0.51655, 0.6621
    ];

    private const double Decay = -0.5;
    private static readonly double Factor = Math.Pow(0.9, 1.0 / Decay) - 1; // 19/81
    private const double MinStability = 0.1;

    private readonly double[] _w;
    private readonly double _desiredRetention;
    private readonly int _maxIntervalDays;
    private readonly TimeSpan[] _learningSteps;
    private readonly TimeSpan[] _relearningSteps;

    public FsrsScheduler(double desiredRetention = 0.9, int maxIntervalDays = 36500,
        TimeSpan[]? learningSteps = null, TimeSpan[]? relearningSteps = null, double[]? weights = null)
    {
        if (desiredRetention is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(desiredRetention));
        _desiredRetention = desiredRetention;
        _maxIntervalDays = maxIntervalDays;
        _learningSteps = learningSteps ?? [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)];
        _relearningSteps = relearningSteps ?? [TimeSpan.FromMinutes(10)];
        _w = weights ?? DefaultWeights;
    }

    public FsrsReviewResult Review(FsrsCard card, Rating rating, DateTime now)
    {
        var elapsedDays = card.LastReview is null ? 0 : Math.Max(0, (now - card.LastReview.Value).TotalDays);
        double s = card.Stability, d = card.Difficulty;
        var state = card.State;
        var step = card.Step;
        var lapses = card.Lapses;
        TimeSpan nextIn;

        switch (state)
        {
            case CardState.New:
                s = InitialStability(rating);
                d = InitialDifficulty(rating);
                (state, step, nextIn) = HandleSteps(CardState.Learning, 0, rating, _learningSteps, s);
                break;

            case CardState.Learning:
            case CardState.Relearning:
                if (elapsedDays < 1)
                    s = ShortTermStability(s, rating);
                else
                    s = rating == Rating.Again
                        ? ForgetStability(d, s, Retrievability(elapsedDays, s))
                        : RecallStability(d, s, Retrievability(elapsedDays, s), rating);
                d = NextDifficulty(d, rating);
                (state, step, nextIn) = HandleSteps(state, step, rating, state == CardState.Learning ? _learningSteps : _relearningSteps, s);
                break;

            case CardState.Review:
            default:
                var r = Retrievability(elapsedDays, s);
                if (elapsedDays < 1)
                    s = ShortTermStability(s, rating);
                else
                    s = rating == Rating.Again ? ForgetStability(d, s, r) : RecallStability(d, s, r, rating);
                d = NextDifficulty(d, rating);

                if (rating == Rating.Again)
                {
                    lapses++;
                    if (_relearningSteps.Length == 0)
                    {
                        nextIn = TimeSpan.FromDays(NextIntervalDays(s));
                    }
                    else
                    {
                        state = CardState.Relearning;
                        step = 0;
                        nextIn = _relearningSteps[0];
                    }
                }
                else
                {
                    nextIn = TimeSpan.FromDays(NextIntervalDays(s));
                }
                break;
        }

        var updated = new FsrsCard(state, s, d, step, card.Reps + 1, lapses, now + nextIn, now);
        return new FsrsReviewResult(updated, elapsedDays, nextIn.TotalDays);
    }

    /// <summary>Butonların altında gösterilecek "10 dk / 3 gün" ön izlemeleri.</summary>
    public IReadOnlyList<(Rating Rating, TimeSpan Interval)> Preview(FsrsCard card, DateTime now) =>
        Enum.GetValues<Rating>().Select(r => (r, Review(card, r, now).Card.Due - now)).ToList();

    public double Retrievability(double elapsedDays, double stability) =>
        stability <= 0 ? 0 : Math.Pow(1 + Factor * elapsedDays / stability, Decay);

    // ---------- Adım (learning steps) yönetimi ----------

    private (CardState, int, TimeSpan) HandleSteps(CardState state, int step, Rating rating, TimeSpan[] steps, double stability)
    {
        if (steps.Length == 0 || rating == Rating.Easy)
            return (CardState.Review, 0, TimeSpan.FromDays(NextIntervalDays(stability)));

        step = Math.Min(step, steps.Length - 1);
        switch (rating)
        {
            case Rating.Again:
                return (state, 0, steps[0]);
            case Rating.Hard:
                if (step == 0)
                    return (state, 0, steps.Length == 1
                        ? TimeSpan.FromTicks((long)(steps[0].Ticks * 1.5))
                        : TimeSpan.FromTicks((steps[0].Ticks + steps[1].Ticks) / 2));
                return (state, step, steps[step]);
            case Rating.Good:
            default:
                if (step + 1 >= steps.Length)
                    return (CardState.Review, 0, TimeSpan.FromDays(NextIntervalDays(stability)));
                return (state, step + 1, steps[step + 1]);
        }
    }

    // ---------- FSRS-5 formülleri ----------

    public int NextIntervalDays(double stability)
    {
        var interval = stability / Factor * (Math.Pow(_desiredRetention, 1 / Decay) - 1);
        return (int)Math.Clamp(Math.Round(interval), 1, _maxIntervalDays);
    }

    private double InitialStability(Rating r) => Math.Max(_w[(int)r - 1], MinStability);

    private double InitialDifficulty(Rating r) => ClampD(_w[4] - Math.Exp(_w[5] * ((int)r - 1)) + 1);

    private double NextDifficulty(double d, Rating r)
    {
        var delta = -_w[6] * ((int)r - 3);
        var damped = d + delta * (10 - d) / 9;                          // doğrusal sönümleme
        var reverted = _w[7] * InitialDifficulty(Rating.Easy) + (1 - _w[7]) * damped; // ortalamaya dönüş
        return ClampD(reverted);
    }

    private double RecallStability(double d, double s, double r, Rating rating)
    {
        var hardPenalty = rating == Rating.Hard ? _w[15] : 1;
        var easyBonus = rating == Rating.Easy ? _w[16] : 1;
        var next = s * (1 + Math.Exp(_w[8]) * (11 - d) * Math.Pow(s, -_w[9]) *
                        (Math.Exp(_w[10] * (1 - r)) - 1) * hardPenalty * easyBonus);
        return Math.Max(next, MinStability);
    }

    private double ForgetStability(double d, double s, double r)
    {
        var longTerm = _w[11] * Math.Pow(d, -_w[12]) * (Math.Pow(s + 1, _w[13]) - 1) * Math.Exp(_w[14] * (1 - r));
        var shortTerm = s / Math.Exp(_w[17] * _w[18]);
        return Math.Max(Math.Min(longTerm, shortTerm), MinStability);
    }

    private double ShortTermStability(double s, Rating r) =>
        Math.Max(s * Math.Exp(_w[17] * ((int)r - 3 + _w[18])), MinStability);

    private static double ClampD(double d) => Math.Clamp(d, 1, 10);

    // ---------- Gösterim ----------

    public static string FormatInterval(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return "<1 dk";
        if (t.TotalMinutes < 60) return $"{Math.Round(t.TotalMinutes)} dk";
        if (t.TotalHours < 24) return $"{Math.Round(t.TotalHours)} sa";
        if (t.TotalDays < 30) return $"{Math.Round(t.TotalDays)} gün";
        if (t.TotalDays < 365) return $"{Math.Round(t.TotalDays / 30, 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',')} ay";
        return $"{Math.Round(t.TotalDays / 365, 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',')} yıl";
    }
}
