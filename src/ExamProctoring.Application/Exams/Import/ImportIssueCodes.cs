namespace ExamProctoring.Application.Exams.Import;

// Stable codes of the problems the Word import reports per question. The import never fixes them itself.
public static class ImportIssueCodes
{
    public const string TextMissing = "text_missing";
    public const string TextTooLong = "text_too_long";
    public const string TooFewOptions = "too_few_options";
    public const string TooManyOptions = "too_many_options";
    public const string OptionTextMissing = "option_text_missing";
    public const string OptionTooLong = "option_too_long";
    public const string DuplicateOptionText = "duplicate_option_text";
    public const string OptionLettersOutOfSequence = "option_letters_out_of_sequence";
    public const string NoCorrectAnswer = "no_correct_answer";
    public const string MultipleCorrectAnswers = "multiple_correct_answers";
    public const string AnswerNotMatchingOption = "answer_not_matching_option";
    public const string ConflictingAnswerMarkers = "conflicting_answer_markers";
    public const string AnswerWithoutOptions = "answer_without_options";
    public const string MarksMissing = "marks_missing";
    public const string MarksInvalid = "marks_invalid";
    public const string ModelAnswerTooLong = "model_answer_too_long";
    public const string UnsupportedContent = "unsupported_content";
    public const string UnrecognizedLines = "unrecognized_lines";
    public const string AmbiguousSplit = "ambiguous_split";
}
