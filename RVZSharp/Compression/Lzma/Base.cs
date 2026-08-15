namespace RVZSharp.Compression.Lzma;

/// <summary>LZMA format constants and the decoder state machine (literal/match/repeat state transitions).</summary>
internal abstract class Base
{
    /// <summary>Number of match distances kept for repeat matches.</summary>
    public const uint K_NUM_REP_DISTANCES = 4;

    /// <summary>Number of LZMA decoder states.</summary>
    public const uint K_NUM_STATES = 12;

    // static byte []kLiteralNextStates  = {0, 0, 0, 0, 1, 2, 3, 4,  5,  6,   4, 5};
    // static byte []kMatchNextStates    = {7, 7, 7, 7, 7, 7, 7, 10, 10, 10, 10, 10};
    // static byte []kRepNextStates      = {8, 8, 8, 8, 8, 8, 8, 11, 11, 11, 11, 11};
    // static byte []kShortRepNextStates = {9, 9, 9, 9, 9, 9, 9, 11, 11, 11, 11, 11};

    /// <summary>Tracks the current decoder state machine state (literal/match/repeat states).</summary>
    public struct State
    {
        /// <summary>The current state index (0-11).</summary>
        public uint Index;

        /// <summary>Resets the state to the initial literal state.</summary>
        public void Init()
        {
            Index = 0;
        }

        /// <summary>Advances the state after decoding a literal byte.</summary>
        public void UpdateChar()
        {
            switch (Index)
            {
                case < 4:
                    Index = 0;
                    break;
                case < 10:
                    Index -= 3;
                    break;
                default:
                    Index -= 6;
                    break;
            }
        }

        /// <summary>Advances the state after decoding a match.</summary>
        public void UpdateMatch()
        {
            Index = (uint)(Index < 7 ? 7 : 10);
        }

        /// <summary>Advances the state after decoding a repeat match.</summary>
        public void UpdateRep()
        {
            Index = (uint)(Index < 7 ? 8 : 11);
        }

        /// <summary>Advances the state after decoding a short repeat match.</summary>
        public void UpdateShortRep()
        {
            Index = (uint)(Index < 7 ? 9 : 11);
        }

        /// <summary>Whether the current state represents a run of decoded literal bytes.</summary>
        /// <returns>True when the state is a literal state.</returns>
        public readonly bool IsCharState()
        {
            return Index < 7;
        }
    }

    /// <summary>Number of bits used to decode a position slot.</summary>
    public const int K_NUM_POS_SLOT_BITS = 6;

    /// <summary>Minimum dictionary log2 size stored in the properties.</summary>
    public const int K_DIC_LOG_SIZE_MIN = 0;

    // public const int kDicLogSizeMax = 30;
    // public const uint kDistTableSizeMax = kDicLogSizeMax * 2;

    /// <summary>Number of bits selecting the position-state from the match length (speed optimization, always 2).</summary>
    public const int K_NUM_LEN_TO_POS_STATES_BITS = 2; // it's for speed optimization

    /// <summary>Number of length-based position states (4).</summary>
    public const uint K_NUM_LEN_TO_POS_STATES = 1 << K_NUM_LEN_TO_POS_STATES_BITS;

    /// <summary>Minimum match length from which length symbols are counted.</summary>
    public const uint K_MATCH_MIN_LEN = 2;

    /// <summary>Maps a match length to its position state used for slot-model selection.</summary>
    /// <param name="len">The match length.</param>
    /// <returns>The position state index, clamped to the last state.</returns>
    public static uint GetLenToPosState(uint len)
    {
        len -= K_MATCH_MIN_LEN;
        if (len < K_NUM_LEN_TO_POS_STATES)
        {
            return len;
        }

        return K_NUM_LEN_TO_POS_STATES - 1;
    }

    /// <summary>Number of low alignment bits of large distances.</summary>
    public const int K_NUM_ALIGN_BITS = 4;

    /// <summary>Number of alignment symbols (2^K_NUM_ALIGN_BITS).</summary>
    public const uint K_ALIGN_TABLE_SIZE = 1 << K_NUM_ALIGN_BITS;

    /// <summary>Bit mask selecting the K_NUM_ALIGN_BITS low bits of a distance.</summary>
    public const uint K_ALIGN_MASK = (K_ALIGN_TABLE_SIZE - 1);

    /// <summary>First position slot whose distance is decoded with the reduced model.</summary>
    public const uint K_START_POS_MODEL_INDEX = 4;

    /// <summary>First position slot whose distance uses direct bits plus the align model.</summary>
    public const uint K_END_POS_MODEL_INDEX = 14;

    /// <summary>Number of reduced position models between the start and end slot indexes.</summary>
    public const uint K_NUM_POS_MODELS = K_END_POS_MODEL_INDEX - K_START_POS_MODEL_INDEX;

    /// <summary>Number of distances covered by the position distance models.</summary>
    public const uint K_NUM_FULL_DISTANCES = 1 << ((int)K_END_POS_MODEL_INDEX / 2);

    /// <summary>Maximum number of literal position bits (lp) supported by the format.</summary>
    public const uint K_NUM_LIT_POS_STATES_BITS_ENCODING_MAX = 4;

    /// <summary>Maximum number of literal context bits (lc) supported by the format.</summary>
    public const uint K_NUM_LIT_CONTEXT_BITS_MAX = 8;

    /// <summary>Maximum number of position-state bits (pb) supported by the format.</summary>
    public const int K_NUM_POS_STATES_BITS_MAX = 4;

    /// <summary>Maximum number of position states (2^pb).</summary>
    public const uint K_NUM_POS_STATES_MAX = (1 << K_NUM_POS_STATES_BITS_MAX);

    /// <summary>Maximum number of position-state bits used by the encoder at property level.</summary>
    public const int K_NUM_POS_STATES_BITS_ENCODING_MAX = 4;

    /// <summary>Maximum number of position states used by the encoder.</summary>
    public const uint K_NUM_POS_STATES_ENCODING_MAX = (1 << K_NUM_POS_STATES_BITS_ENCODING_MAX);

    /// <summary>Number of bits of the low partial length model.</summary>
    public const int K_NUM_LOW_LEN_BITS = 3;

    /// <summary>Number of bits of the mid partial length model.</summary>
    public const int K_NUM_MID_LEN_BITS = 3;

    /// <summary>Number of bits of the high length model.</summary>
    public const int K_NUM_HIGH_LEN_BITS = 8;

    /// <summary>Number of symbols in the low length partial models.</summary>
    public const uint K_NUM_LOW_LEN_SYMBOLS = 1 << K_NUM_LOW_LEN_BITS;

    /// <summary>Number of symbols in the mid length partial models.</summary>
    public const uint K_NUM_MID_LEN_SYMBOLS = 1 << K_NUM_MID_LEN_BITS;

    /// <summary>Total number of length symbols (low + mid + high).</summary>
    public const uint K_NUM_LEN_SYMBOLS =
        K_NUM_LOW_LEN_SYMBOLS + K_NUM_MID_LEN_SYMBOLS + (1 << K_NUM_HIGH_LEN_BITS);

    /// <summary>Maximum match length the length model can represent.</summary>
    public const uint K_MATCH_MAX_LEN = K_MATCH_MIN_LEN + K_NUM_LEN_SYMBOLS - 1;
}
