using System;
using System.Threading;

namespace VisualHFT.Studies.MarketRatios.Model
{
    /// <summary>
    /// Cumulative added, removed and traded volume for the current counting interval, backing the
    /// volume form of the Order-to-Trade Ratio.
    ///
    /// WHAT AN INTERVAL IS
    /// An interval opens on the first order-book reading and runs until something makes readings
    /// before and after it incomparable: the UTC day turning, or the size scale changing. Either
    /// one closes the interval, returns all three totals to zero together and opens a new one.
    /// Totals are never carried across that boundary.
    ///
    /// WHY THE TOTALS RUN FOR A WHOLE INTERVAL, NOT PER AGGREGATION WINDOW
    /// A ratio computed over a single aggregation window goes negative under perfectly normal
    /// conditions: liquidity already resting in the book when the window opens is never counted
    /// as added volume, yet its later execution is counted as both removed volume and traded
    /// volume. Accumulating from the arming instant is what makes the subtraction balance.
    ///
    /// ARMING
    /// The accumulator counts nothing until its first order-book reading, which seeds the
    /// baseline and contributes no volume of its own. A trade arriving before that reading
    /// belongs to no measurable interval, so it is dropped rather than buffered, and counted in
    /// <see cref="DroppedPreArmingTradeCount"/>. The same rule applies at the daily rollover.
    ///
    /// PRECISION
    /// The three totals are kept as integers scaled by 10^sizeDecimalPlaces and advanced with
    /// <see cref="Interlocked"/>. Each order-book delta is converted once, on arrival, so the
    /// rounding error of a reading is bounded per callback instead of growing with the running
    /// total. This bounds that error; it does not remove the inexactness already present in the
    /// double-valued readings the book hands out.
    ///
    /// WHAT THE REMOVED-VOLUME READING DOES NOT COVER
    /// The book credits removed volume when a price level is deleted or shrinks, but not when a
    /// level falls out of the tracked depth because a better-priced one displaced it. Size can
    /// therefore leave the book without ever being counted as removed, and a level that leaves
    /// the tracked depth and later returns is counted as added again. The identity computed here
    /// — ordered volume as added plus removed less traded — is approximate for that reason. How
    /// large the discrepancy is, and whether it raises or lowers the result, follow from how much
    /// the tracked depth churns on the feed in use. Neither is measured here, and neither should
    /// be asserted without measuring it.
    ///
    /// THREAD SAFETY
    /// The order-book stream and the trade stream are independent dispatchers: their callbacks
    /// can run at the same time, on different threads, with no ordering guarantee between a
    /// trade and the book change that produced it. Running totals are therefore order-
    /// insensitive sums advanced only through <see cref="Interlocked"/>. The baseline and
    /// interval fields have two writers and no lock between them: <see cref="OnOrderBookCounters"/>,
    /// which carries the matching single-thread requirement on its caller, and
    /// <see cref="Reset"/>, which must not run while either callback can. The trade path reads
    /// interval state but never writes it.
    /// </summary>
    internal sealed class VolumeIntervalAccumulator
    {
        private const int NotArmed = 0;
        private const int Arming = 1;
        private const int ArmedState = 2;

        /// <summary>Highest supported scale exponent, matching the book's own scale table.</summary>
        private const int MaxSizeDecimalPlaces = 18;

        // Baseline of the last order-book reading. Two writers, no lock between them: the
        // order-book path and its interval transitions, and Reset, which the caller must keep
        // away from the callbacks. Nothing else touches these.
        private double _previousAddedVolume;
        private double _previousDeletedVolume;

        // Interval state. Written during an interval transition, which only the order-book path
        // performs, and by Reset under the same caller obligation; read by both callbacks, so
        // every read is atomic or volatile.
        private int _armState = NotArmed;
        private int _sizeDecimalPlaces;
        private long _scale = 1L;
        private long _intervalUtcDayTicks;
        private long _armedAtUtcTicks;

        // Running totals. Written by BOTH callbacks: interlocked access only, never a plain
        // read-modify-write.
        private long _addedScaled;
        private long _deletedScaled;
        private long _tradedScaled;

        private long _droppedPreArmingTrades;

        /// <summary>True once a first order-book reading has seeded the baseline.</summary>
        public bool IsArmed => Volatile.Read(ref _armState) == ArmedState;

        /// <summary>
        /// The instant the current counting interval began, or null while the accumulator has
        /// never been armed. The daily rollover and a change of size scale both start a new
        /// interval and move this forward.
        /// </summary>
        public DateTime? ArmedAtUtc
        {
            get
            {
                long ticks = Interlocked.Read(ref _armedAtUtcTicks);
                return ticks == 0L ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// How many trades arrived with no interval to attribute them to — before the first
        /// order-book reading, or after the day turned but before the first reading of the new
        /// day. Those trades are dropped, never counted late.
        /// </summary>
        public long DroppedPreArmingTradeCount => Interlocked.Read(ref _droppedPreArmingTrades);

        /// <summary>
        /// Takes one cumulative order-book volume reading and banks the volume added and removed
        /// since the previous reading.
        ///
        /// Three readings bank nothing and seed a baseline instead: the first one after arming,
        /// the first one after the UTC day changes, and the first one after the size scale
        /// changes. The last two also return all three totals to zero, closing one interval and
        /// opening another. A reading that falls below the baseline is taken as proof the book
        /// cleared its own counters; that difference is discarded and the baseline moves on.
        ///
        /// The caller must invoke this from one thread at a time. The dispatcher does not
        /// serialise subscriber callbacks, so this is a requirement on the producer, not a
        /// property the accumulator can enforce.
        /// </summary>
        /// <param name="addedVolume">
        /// The book's cumulative added volume, in size units at <paramref name="sizeDecimalPlaces"/>
        /// precision. Monotone non-decreasing while the book keeps its contents.
        /// </param>
        /// <param name="deletedVolume">
        /// The book's cumulative removed volume, in the same units and with the same monotonicity.
        /// It counts levels deleted or shrunk, not levels displaced out of the tracked depth, so
        /// it under-counts the size that actually left the book.
        /// </param>
        /// <param name="sizeDecimalPlaces">
        /// Decimal places of size precision for this symbol. A change of this value closes the
        /// current interval, because readings either side of it are in different units.
        /// </param>
        /// <param name="utcNow">
        /// The current instant. Normalised to UTC on arrival, so a local or unspecified value is
        /// converted rather than silently treated as UTC.
        /// </param>
        public void OnOrderBookCounters(double addedVolume, double deletedVolume, int sizeDecimalPlaces, DateTime utcNow)
        {
            utcNow = AsUtc(utcNow);

            if (!EnsureArmed(addedVolume, deletedVolume, sizeDecimalPlaces, utcNow))
                return;

            // The day turned: close the interval and open a new one from this reading.
            if (utcNow.Date.Ticks != Interlocked.Read(ref _intervalUtcDayTicks))
            {
                StartNewInterval(addedVolume, deletedVolume, sizeDecimalPlaces, utcNow);
                return;
            }

            // The size scale changed, so readings before and after are in different units and
            // their difference is not a volume. Start a new interval rather than report the
            // change of units as a jump in activity.
            if (sizeDecimalPlaces != Volatile.Read(ref _sizeDecimalPlaces))
            {
                StartNewInterval(addedVolume, deletedVolume, sizeDecimalPlaces, utcNow);
                return;
            }

            // The book's counters only ever climb while it keeps its contents; clearing the book
            // on a reconnect or a fresh snapshot zeroes them. A reading below the baseline is
            // therefore proof of such a clear. The difference across that boundary is not a
            // volume, so it is discarded and the baseline moves to the new reading. Volume
            // already banked stays banked.
            if (addedVolume < _previousAddedVolume || deletedVolume < _previousDeletedVolume)
            {
                _previousAddedVolume = addedVolume;
                _previousDeletedVolume = deletedVolume;
                return;
            }

            double addedDelta = addedVolume - _previousAddedVolume;
            double deletedDelta = deletedVolume - _previousDeletedVolume;
            _previousAddedVolume = addedVolume;
            _previousDeletedVolume = deletedVolume;

            long scale = Interlocked.Read(ref _scale);

            long addedScaled = ToScaledUnits(addedDelta, scale);
            if (addedScaled != 0L)
                Interlocked.Add(ref _addedScaled, addedScaled);

            long deletedScaled = ToScaledUnits(deletedDelta, scale);
            if (deletedScaled != 0L)
                Interlocked.Add(ref _deletedScaled, deletedScaled);
        }

        /// <summary>
        /// Banks one public trade print against the current interval.
        ///
        /// The trade is discarded, not buffered, when there is no interval to attribute it to:
        /// before the interval's first order-book reading, or after the UTC day has turned but
        /// before the first reading of the new day has opened the next interval. Discarded trades
        /// are counted in <see cref="DroppedPreArmingTradeCount"/> and never counted late.
        /// </summary>
        /// <param name="size">
        /// The size that traded, in the same size units the order-book readings are expressed in.
        /// Values at or below zero are ignored.
        /// </param>
        /// <param name="utcNow">
        /// The current instant. Normalised to UTC on arrival, so a local or unspecified value is
        /// converted rather than silently treated as UTC.
        /// </param>
        public void OnTrade(decimal size, DateTime utcNow)
        {
            utcNow = AsUtc(utcNow);

            if (Volatile.Read(ref _armState) != ArmedState)
            {
                Interlocked.Increment(ref _droppedPreArmingTrades);
                return;
            }

            // The day has turned but no order-book reading has opened the new interval yet. The
            // trade belongs to neither interval, so it is dropped on the same rule that drops a
            // trade arriving before the first reading.
            if (utcNow.Date.Ticks != Interlocked.Read(ref _intervalUtcDayTicks))
            {
                Interlocked.Increment(ref _droppedPreArmingTrades);
                return;
            }

            if (size <= 0m)
                return;

            long scaled = ToScaledUnits(size, Interlocked.Read(ref _scale));
            if (scaled != 0L)
                Interlocked.Add(ref _tradedScaled, scaled);
        }

        /// <summary>
        /// Reads the ordered and traded volume of the current interval.
        /// </summary>
        /// <param name="orderedVolume">max(added + removed - traded, 0).</param>
        /// <param name="tradedVolume">Traded volume, always above zero when this returns true.</param>
        /// <param name="numeratorClamped">
        /// True when added + removed came out below traded and the ordered volume was floored at
        /// zero. That happens when the counters were reset or hit a precision edge, so a zero
        /// here does not mean there was no activity. Whether the ratio a caller derives needs
        /// flooring as well is the caller's own rule, not this one.
        /// </param>
        /// <returns>
        /// False while the accumulator is unarmed or no public trade has been seen, in which case
        /// there is no value to publish at all. Callers must not substitute a floor for the
        /// denominator to reach a number.
        /// </returns>
        public bool TryGetIntervalVolumes(out decimal orderedVolume, out decimal tradedVolume, out bool numeratorClamped)
        {
            orderedVolume = 0m;
            tradedVolume = 0m;
            numeratorClamped = false;

            if (Volatile.Read(ref _armState) != ArmedState)
                return false;

            // Traded is read first on purpose. The three totals advance independently, so this
            // is not an instantaneous snapshot; reading the subtrahend first biases the
            // numerator upwards rather than downwards and avoids a clamp that only reflects
            // read order.
            long traded = Interlocked.Read(ref _tradedScaled);
            if (traded <= 0L)
                return false;

            long added = Interlocked.Read(ref _addedScaled);
            long deleted = Interlocked.Read(ref _deletedScaled);

            long ordered = added + deleted - traded;
            numeratorClamped = ordered < 0L;
            if (numeratorClamped)
                ordered = 0L;

            decimal scale = Interlocked.Read(ref _scale);
            orderedVolume = ordered / scale;
            tradedVolume = traded / scale;
            return true;
        }

        /// <summary>
        /// Returns the accumulator to its unarmed state, discarding every total and every
        /// diagnostic count. Used when the study starts a fresh run.
        ///
        /// Must not run while either callback can. It writes the baseline and interval fields
        /// that the order-book path otherwise owns, so the caller resets before subscribing, or
        /// after unsubscribing — never alongside a live stream.
        /// </summary>
        public void Reset()
        {
            Volatile.Write(ref _armState, NotArmed);

            Interlocked.Exchange(ref _addedScaled, 0L);
            Interlocked.Exchange(ref _deletedScaled, 0L);
            Interlocked.Exchange(ref _tradedScaled, 0L);
            Interlocked.Exchange(ref _droppedPreArmingTrades, 0L);
            Interlocked.Exchange(ref _armedAtUtcTicks, 0L);
            Interlocked.Exchange(ref _intervalUtcDayTicks, 0L);
            Interlocked.Exchange(ref _scale, 1L);

            Volatile.Write(ref _sizeDecimalPlaces, 0);
            _previousAddedVolume = 0d;
            _previousDeletedVolume = 0d;
        }

        /// <summary>
        /// Arms the accumulator if it is not already armed, seeding the baseline from this
        /// reading. Private: arming is a consequence of taking a reading, never something a
        /// caller chooses, and seeding a baseline from outside the order-book path would write
        /// single-writer state from an arbitrary thread.
        /// </summary>
        /// <returns>
        /// True when a delta may be measured against an existing baseline. False when this call
        /// performed the arming, or another thread is performing it, in which case the reading is
        /// a baseline and contributes no volume.
        /// </returns>
        private bool EnsureArmed(double addedVolume, double deletedVolume, int sizeDecimalPlaces, DateTime utcNow)
        {
            int observed = Volatile.Read(ref _armState);
            if (observed == ArmedState)
                return true;

            if (Interlocked.CompareExchange(ref _armState, Arming, NotArmed) != NotArmed)
                return false;   // another thread owns the transition; this reading contributes nothing

            SeedInterval(addedVolume, deletedVolume, sizeDecimalPlaces, utcNow);
            Volatile.Write(ref _armState, ArmedState);
            return false;
        }

        /// <summary>
        /// Closes the current interval and opens a new one from this reading: the three totals
        /// go to zero together, so added, removed and traded volume always describe the same
        /// interval. Called only from the order-book path.
        /// </summary>
        private void StartNewInterval(double addedVolume, double deletedVolume, int sizeDecimalPlaces, DateTime utcNow)
        {
            // Stop counting trades first, so a trade in flight cannot land between the reset and
            // the new baseline.
            Volatile.Write(ref _armState, Arming);

            Interlocked.Exchange(ref _addedScaled, 0L);
            Interlocked.Exchange(ref _deletedScaled, 0L);
            Interlocked.Exchange(ref _tradedScaled, 0L);

            SeedInterval(addedVolume, deletedVolume, sizeDecimalPlaces, utcNow);
            Volatile.Write(ref _armState, ArmedState);
        }

        private void SeedInterval(double addedVolume, double deletedVolume, int sizeDecimalPlaces, DateTime utcNow)
        {
            _previousAddedVolume = addedVolume;
            _previousDeletedVolume = deletedVolume;

            Volatile.Write(ref _sizeDecimalPlaces, sizeDecimalPlaces);
            Interlocked.Exchange(ref _scale, ComputeScale(sizeDecimalPlaces));
            Interlocked.Exchange(ref _intervalUtcDayTicks, utcNow.Date.Ticks);
            Interlocked.Exchange(ref _armedAtUtcTicks, utcNow.Ticks);
        }

        /// <summary>
        /// Normalises an instant to UTC. A DateTime cannot carry a guarantee that it already is
        /// UTC, and the day boundary this class keys on is only a real boundary if it is. An
        /// unspecified instant is read as local, which is what the framework conversion does.
        /// </summary>
        private static DateTime AsUtc(DateTime instant)
        {
            return instant.Kind == DateTimeKind.Utc ? instant : instant.ToUniversalTime();
        }

        // Mirrors the scale table the order book uses for the same size precision. The rounding
        // below differs on purpose: the book truncates when it scales a raw size, while the input
        // here is a difference between two already-quantised readings, so rounding half away from
        // zero recovers the exact integer the subtraction was meant to produce.
        private static long ComputeScale(int sizeDecimalPlaces)
        {
            if (sizeDecimalPlaces <= 0)
                return 1L;

            int places = sizeDecimalPlaces > MaxSizeDecimalPlaces ? MaxSizeDecimalPlaces : sizeDecimalPlaces;
            long scale = 1L;
            for (int i = 0; i < places; i++)
                scale *= 10L;

            return scale;
        }

        private static long ToScaledUnits(double volume, long scale)
        {
            if (!(volume > 0d))
                return 0L;   // also rejects NaN

            double scaled = Math.Round(volume * scale, MidpointRounding.AwayFromZero);
            if (!(scaled > 0d))
                return 0L;
            if (scaled >= long.MaxValue)
                return long.MaxValue;

            return (long)scaled;
        }

        private static long ToScaledUnits(decimal volume, long scale)
        {
            if (volume <= 0m)
                return 0L;

            decimal scaled = Math.Round(volume * scale, MidpointRounding.AwayFromZero);
            if (scaled >= long.MaxValue)
                return long.MaxValue;

            return (long)scaled;
        }
    }
}
