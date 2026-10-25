using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExamProctoring.Domain.Exams;

namespace ExamProctoring.Application.Exams.Import;

// The documented format of the Word import (owner decision 2026-10-24; frontend handover D.12.3), in English and
// Arabic. It turns lines into questions and reports every problem instead of guessing: it never invents a correct
// answer, marks, options, a question type or question text. Lines that match no rule are kept as unassigned text.
public static partial class QuestionImportParser
{
    // Option letters in their order: A-H, and the Arabic abjad letters used for options.
    private const string LatinLetters = "ABCDEFGH";
    private const string ArabicLetters = "أبجدهوزح";

    public static DocxImportResponse Parse(IReadOnlyList<DocxItem> items, int maxMcqOptions)
    {
        var questions = new List<Builder>();
        var unassigned = new List<string>();
        var unsupported = new List<UnsupportedContentItem>();
        Builder? current = null;

        for (var position = 0; position < items.Count; position++)
        {
            var item = items[position];
            if (item.Unsupported is { } kind)
            {
                unsupported.Add(new UnsupportedContentItem(current?.Index, kind.ToString()));
                current?.UnsupportedKinds.Add(kind);
                unassigned.AddRange(item.UnsupportedText.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text.Trim()));
                continue;
            }

            var line = Line.From(item.Text);
            if (line.Original.Length == 0)
                continue;

            if ((Match(QuestionStartPattern(), line) ?? Match(ArabicQuestionStartPattern(), line)) is { } start)
            {
                var number = int.Parse(start.Groups["number"].Value, CultureInfo.InvariantCulture);
                var ambiguous = false;

                // Only a plain number ("2.") can be a list item; "Q2", "Question 2" and "س2" always start a question.
                if (current is { State: State.ModelAnswer } && !start.Groups["prefix"].Success
                    && current.ContinuesModelAnswerList(number))
                {
                    var segment = HasInlineMarks(line.Rest(start.Groups["text"]))
                        ? Segment.InlineMarks
                        : ReadSegment(items, position + 1);
                    if (segment == Segment.ModelAnswerLine)
                    {
                        // "1. Evaporation" "2. Condensation" inside a model answer: a list, not new questions.
                        current.AddModelAnswerListLine(number, line.Original);
                        continue;
                    }

                    // Marks on the line itself ("2. Define condensation. (2 marks)") may be a question with its marks
                    // or a marking-scheme point of this model answer. Only a marks line follows and the question has
                    // no marks yet: the marks may close this model answer or belong to a new question. Either way the
                    // line starts a new question and both questions are reported instead of choosing.
                    ambiguous = segment == Segment.InlineMarks
                                || (segment == Segment.MarksOnly && current.MarksValues.Count == 0);
                    if (ambiguous)
                        current.AmbiguousSplitAfterModelAnswer = true;
                }
                else if (current is { SplitFromModelAnswer: true, SourceLines.Count: 1 } && !start.Groups["prefix"].Success)
                {
                    // The previous question was split from a model answer and is only its numbered line: this line
                    // may be the next point of the same marking scheme, so it is reported too.
                    ambiguous = true;
                }

                current?.QuestionStarted(number);
                current = new Builder(questions.Count + 1, number)
                {
                    AmbiguousSplitAfterModelAnswer = ambiguous,
                    SplitFromModelAnswer = ambiguous,
                };
                questions.Add(current);
                current.SourceLines.Add(line.Original);
                current.AddText(line.Rest(start.Groups["text"]));
                continue;
            }

            if (current is null)
            {
                unassigned.Add(line.Original);
                continue;
            }

            current.SourceLines.Add(line.Original);

            if ((Match(ModelAnswerPattern(), line) ?? Match(ArabicModelAnswerPattern(), line)) is { } model)
            {
                current.ModelAnswerLines.Add(line.Rest(model.Groups["value"]));
                current.State = State.ModelAnswer;
            }
            else if ((Match(AnswerPattern(), line) ?? Match(ArabicAnswerPattern(), line)) is { } answer)
            {
                current.AnswerValues.Add(line.Rest(answer.Groups["value"]));
                current.State = State.AfterAnswer;
            }
            else if ((Match(MarksPattern(), line) ?? Match(ArabicMarksPattern(), line)) is { } marks)
            {
                current.AddMarks(marks.Groups["value"].Value);
            }
            else if (current.State is State.Text or State.Options
                     && ((Match(OptionPattern(), line) ?? Match(ArabicOptionPattern(), line)) is { } option))
            {
                current.Options.Add(new RawOption(
                    LetterIndex(option.Groups["letter"].Value),
                    option.Groups["star"].Success,
                    line.Rest(option.Groups["text"])));
                current.State = State.Options;
            }
            else if (current.State == State.Text)
            {
                current.AddText(line.Original);
            }
            else if (current.State == State.ModelAnswer)
            {
                current.ModelAnswerLines.Add(line.Original);
            }
            else
            {
                // After the options or the answer line, an unknown line could belong to an option, the answer or a
                // missing next question: keep it visible and let the instructor decide.
                current.UnrecognizedLines = true;
                unassigned.Add(line.Original);
            }
        }

        var built = questions.Select(builder => builder.Build(maxMcqOptions)).ToList();
        var summary = new ImportSummary(
            built.Count,
            built.Count(q => q.Status == ImportStatus.Ready),
            built.Count(q => q.Status == ImportStatus.NeedsAttention),
            built.Count(q => q.Status == ImportStatus.NotImported));
        return new DocxImportResponse(summary, built, unassigned, unsupported);
    }

    // What follows a numbered line inside a model answer, up to the next numbered line.
    private enum Segment
    {
        // Plain text only: the numbered line is part of the model answer.
        ModelAnswerLine,

        // Options, an answer line or a model answer: the numbered line starts a question.
        Question,

        // Plain text and marks only.
        MarksOnly,

        // Marks on the numbered line itself.
        InlineMarks
    }

    private static Segment ReadSegment(IReadOnlyList<DocxItem> items, int from)
    {
        var marks = false;
        for (var i = from; i < items.Count; i++)
        {
            if (items[i].Unsupported is not null)
                continue;
            var line = Line.From(items[i].Text);
            if (line.Original.Length == 0)
                continue;
            if ((Match(QuestionStartPattern(), line) ?? Match(ArabicQuestionStartPattern(), line)) is not null)
                break;
            if ((Match(OptionPattern(), line) ?? Match(ArabicOptionPattern(), line)
                 ?? Match(AnswerPattern(), line) ?? Match(ArabicAnswerPattern(), line)
                 ?? Match(ModelAnswerPattern(), line) ?? Match(ArabicModelAnswerPattern(), line)) is not null)
                return Segment.Question;
            if ((Match(MarksPattern(), line) ?? Match(ArabicMarksPattern(), line)) is not null || HasInlineMarks(line.Original))
                marks = true;
        }
        return marks ? Segment.MarksOnly : Segment.ModelAnswerLine;
    }

    // "(2 marks)" or "(2 درجة)" at the end of the text.
    private static bool HasInlineMarks(string text) => InlineMarksPattern().IsMatch(Line.From(text).Normalized);

    private static Match? Match(Regex pattern, Line line)
    {
        var match = pattern.Match(line.Normalized);
        return match.Success ? match : null;
    }

    // 1-based position of an option letter in its alphabet.
    private static int LetterIndex(string letter)
    {
        var latin = LatinLetters.IndexOf(char.ToUpperInvariant(letter[0]));
        return latin >= 0 ? latin + 1 : ArabicLetters.IndexOf(letter[0]) + 1;
    }

    // ---- Patterns. Matched against the normalized line (Western digits, no bidi controls); text groups are taken
    // from the original line at the same positions, so Arabic-Indic digits in question text are kept as written.

    // "1." "1)" "1-" "1:" "Q1." "Question 1:". A digit right after the separator is a number, not a question:
    // "2.5 kg", "12-10-2026", "10:30".
    [GeneratedRegex(@"^(?:(?<prefix>question|q)\s*)?(?<number>\d{1,3})\s*(?:[.):]|\s[-–]|-)(?!\d)\s*(?<text>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionStartPattern();

    // "س1" "س1." "سؤال 1:"
    [GeneratedRegex(@"^(?<prefix>سؤال|س)\s*(?<number>\d{1,3})\s*(?:[.):]|[-–])?\s*(?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ArabicQuestionStartPattern();

    // "A) text" "a. text" "(B) text" "C - text" "*D) text"
    [GeneratedRegex(@"^(?<star>\*)?\s*\(?(?<letter>[A-Ha-h])(?:\)|\.|\s*[-–]\s)\s*(?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex OptionPattern();

    // "أ) text" "ب. text" "ج- text" "*د) text"
    [GeneratedRegex(@"^(?<star>\*)?\s*\(?(?<letter>[أبجدهوزح])\s*(?:\)|\.|[-–])\s*(?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ArabicOptionPattern();

    // "Answer: B" "Correct answer: B" "Ans - B"
    [GeneratedRegex(@"^(?:correct\s+answer|answer|ans)\s*[:：\-–]\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnswerPattern();

    // "الإجابة: ب" "الإجابة الصحيحة: ب"
    [GeneratedRegex(@"^(?:الإجابة|الاجابة)(?:\s+الصحيحة)?\s*[:：\-–]\s*(?<value>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ArabicAnswerPattern();

    // "Model answer: ..." "Sample answer: ..."
    [GeneratedRegex(@"^(?:model|sample)\s+answer\s*[:：\-–]\s*(?<value>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelAnswerPattern();

    // "الإجابة النموذجية: ..."
    [GeneratedRegex(@"^(?:الإجابة|الاجابة)\s+النموذجية\s*[:：\-–]\s*(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ArabicModelAnswerPattern();

    // "Marks: 2" "Mark: 1.5" "Points: 3"
    [GeneratedRegex(@"^(?:marks?|points?)\s*[:：\-–]\s*(?<value>\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarksPattern();

    // "الدرجة: 2" "الدرجات: 2" "درجة: 2"
    [GeneratedRegex(@"^(?:الدرجة|الدرجات|درجة)\s*[:：\-–]\s*(?<value>\S+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ArabicMarksPattern();

    // "(2 marks)" at the end of a question line, also Arabic "(2 درجة)".
    [GeneratedRegex(@"\(\s*(?<value>\d+(?:\.\d+)?)\s*(?:marks?|points?|درجة|درجات)\s*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineMarksPattern();

    // A single option letter as an answer: "B", "b)", "(B)", "B.", "ب".
    [GeneratedRegex(@"^\(?(?<letter>[A-Ha-h]|[أبجدهوزح])\s*[.)]?$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleLetterPattern();

    // Several letters: "A, C" "A and C" "B & D" "أ، ج" "أ و ج".
    [GeneratedRegex(@"^\(?([A-Ha-h]|[أبجدهوزح])\)?(?:\s*(?:,|،|&|/|\band\b|\s+و\s+)\s*\(?([A-Ha-h]|[أبجدهوزح])\)?)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeveralLettersPattern();

    // ---- Lines

    private readonly record struct Line(string Original, string Normalized)
    {
        public static Line From(string? text)
        {
            var original = StripBidiControls(text ?? string.Empty).Trim();
            return new Line(original, NormalizeDigits(original));
        }

        public string Rest(Group group) => group.Success ? Original.Substring(group.Index, group.Length).Trim() : string.Empty;

        private static string StripBidiControls(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c is '‎' or '‏' or '؜' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))
                    continue;
                builder.Append(c);
            }
            return builder.ToString();
        }

        // Same length as the input, so positions found in the normalized line are valid in the original line.
        private static string NormalizeDigits(string text)
        {
            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = chars[i] switch
                {
                    >= '٠' and <= '٩' => (char)('0' + (chars[i] - '٠')),
                    >= '۰' and <= '۹' => (char)('0' + (chars[i] - '۰')),
                    '٫' => '.',
                    _ => chars[i],
                };
            }
            return new string(chars);
        }
    }

    // ---- Building one question

    private enum State
    {
        Text,
        Options,
        AfterAnswer,
        ModelAnswer
    }

    private sealed record RawOption(int LetterIndex, bool Starred, string Text);

    private sealed class Builder(int index, int number)
    {
        public int Index { get; } = index;

        // The number written in the document ("3." gives 3).
        public int Number { get; } = number;
        public State State { get; set; } = State.Text;
        public List<string> SourceLines { get; } = [];
        public List<string> TextLines { get; } = [];
        public List<RawOption> Options { get; } = [];
        public List<string> AnswerValues { get; } = [];
        public List<string> ModelAnswerLines { get; } = [];
        public List<string> MarksValues { get; } = [];
        public HashSet<UnsupportedContentKind> UnsupportedKinds { get; } = [];
        public bool UnrecognizedLines { get; set; }

        // The last number of a numbered list inside the model answer ("1. ..." "2. ..."), or 0.
        private int _modelAnswerListEnd;

        // A list in a model answer starts at 1 and goes up one by one; any other number starts a new question.
        public bool ContinuesModelAnswerList(int lineNumber) => lineNumber == _modelAnswerListEnd + 1;

        public bool AmbiguousSplitAfterModelAnswer { get; set; }

        // The question was started by a doubtful numbered line of the previous question's model answer.
        public bool SplitFromModelAnswer { get; init; }

        // A list line of the model answer that has the next question's number ("2. Condensation" in question 1).
        // It stays doubtful until a question with that number follows; otherwise the question is reported, so a
        // question read into the model answer can never be silently Ready.
        private int? _possibleNextQuestion;

        public void AddModelAnswerListLine(int lineNumber, string line)
        {
            _modelAnswerListEnd = lineNumber;
            if (lineNumber == Number + 1)
                _possibleNextQuestion = lineNumber;
            SourceLines.Add(line);
            ModelAnswerLines.Add(line);
        }

        // The next question starts with this number.
        public void QuestionStarted(int nextNumber)
        {
            if (_possibleNextQuestion == nextNumber)
                _possibleNextQuestion = null;
        }

        public void AddText(string text)
        {
            var inline = InlineMarksPattern().Match(NormalizeForMarks(text));
            if (inline.Success)
            {
                MarksValues.Add(inline.Groups["value"].Value);
                text = text[..inline.Index].TrimEnd();
            }
            if (text.Length > 0)
                TextLines.Add(text);
        }

        public void AddMarks(string value) => MarksValues.Add(value);

        public ImportedQuestion Build(int maxMcqOptions)
        {
            var issues = new List<ImportIssue>();
            var text = string.Join("\n", TextLines).Trim();
            var modelAnswer = ModelAnswerLines.Count == 0 ? null : string.Join("\n", ModelAnswerLines).Trim();
            if (modelAnswer is { Length: 0 })
                modelAnswer = null;
            var sourceText = string.Join("\n", SourceLines);

            if (text.Length == 0 && Options.Count == 0)
            {
                Add(issues, ImportIssueCodes.TextMissing, "The question has no text and no options.");
                return new ImportedQuestion(Index, ImportStatus.NotImported, null, string.Empty, null, null, [], issues, sourceText);
            }

            var isMcq = Options.Count > 0;
            var correct = isMcq ? ResolveCorrectOptions(issues) : NoAnswerForEssay(issues);
            var options = Options
                .Select((option, i) => new ImportedOption(option.Text, correct.Contains(i)))
                .ToList();

            // Question text (D31).
            if (text.Length == 0)
                Add(issues, ImportIssueCodes.TextMissing, "The question has no text.");
            else if (text.Length > ExamFieldLimits.QuestionTextMaxLength)
                Add(issues, ImportIssueCodes.TextTooLong, $"The question text is longer than {ExamFieldLimits.QuestionTextMaxLength} characters.");

            // Options (D32), with the same rules as QuestionRequestValidator.
            if (isMcq)
            {
                var domainOptions = options.Select(o => new QuestionOption { Text = o.Text, IsCorrect = o.IsCorrect }).ToList();
                if (!QuestionRules.HasEnoughOptions(domainOptions))
                    Add(issues, ImportIssueCodes.TooFewOptions, $"A multiple-choice question needs at least {QuestionRules.MinMcqOptions} options.");
                if (!QuestionRules.HasAtMostOptions(domainOptions, maxMcqOptions))
                    Add(issues, ImportIssueCodes.TooManyOptions, $"The question has {options.Count} options; the maximum is {maxMcqOptions}.");
                if (options.Any(o => o.Text.Trim().Length == 0))
                    Add(issues, ImportIssueCodes.OptionTextMissing, "An option has no text.");
                if (options.Any(o => o.Text.Trim().Length > ExamFieldLimits.OptionTextMaxLength))
                    Add(issues, ImportIssueCodes.OptionTooLong, $"An option is longer than {ExamFieldLimits.OptionTextMaxLength} characters.");
                if (!QuestionRules.HasDistinctOptionTexts(domainOptions.Where(o => o.Text.Trim().Length > 0)))
                    Add(issues, ImportIssueCodes.DuplicateOptionText, "Two options have the same text.");
                CheckLetterSequence(issues);
            }

            // Marks (D31, D35): never filled in when missing.
            var marks = ResolveMarks(issues);

            if (modelAnswer is { Length: > ExamFieldLimits.ModelAnswerMaxLength })
                Add(issues, ImportIssueCodes.ModelAnswerTooLong, $"The model answer is longer than {ExamFieldLimits.ModelAnswerMaxLength} characters.");

            foreach (var kind in UnsupportedKinds.Order())
                Add(issues, ImportIssueCodes.UnsupportedContent, $"The question contains content that cannot be imported ({kind}).");

            if (AmbiguousSplitAfterModelAnswer)
                Add(issues, ImportIssueCodes.AmbiguousSplit, "A numbered line after a model answer may be part of the model answer or a new question.");
            if (_possibleNextQuestion is { } possible)
                Add(issues, ImportIssueCodes.AmbiguousSplit,
                    string.Create(CultureInfo.InvariantCulture, $"The model answer has a line numbered {possible}, and no question {possible} follows; it may be a question."));

            if (UnrecognizedLines)
                Add(issues, ImportIssueCodes.UnrecognizedLines, "Some lines of this question match no rule; they are listed under unassigned text.");

            return new ImportedQuestion(
                Index,
                issues.Count == 0 ? ImportStatus.Ready : ImportStatus.NeedsAttention,
                isMcq ? nameof(QuestionType.Mcq) : nameof(QuestionType.Essay),
                text,
                marks,
                modelAnswer,
                options,
                issues,
                sourceText);
        }

        // The options marked correct, by "*" and/or by answer lines. Conflicts and unknown answers are reported, and
        // then no option is marked correct rather than one being chosen.
        private HashSet<int> ResolveCorrectOptions(List<ImportIssue> issues)
        {
            var starred = Options.Select((option, i) => (option, i)).Where(x => x.option.Starred).Select(x => x.i).ToHashSet();

            var answered = new HashSet<int>();
            var unknownAnswer = false;
            foreach (var value in AnswerValues)
            {
                foreach (var target in AnswerTargets(value))
                {
                    if (target is { } i)
                        answered.Add(i);
                    else
                        unknownAnswer = true;
                }
            }

            if (unknownAnswer)
                Add(issues, ImportIssueCodes.AnswerNotMatchingOption, "The answer line does not match any option.");

            HashSet<int> correct;
            if (starred.Count > 0 && answered.Count > 0 && !starred.SetEquals(answered))
            {
                Add(issues, ImportIssueCodes.ConflictingAnswerMarkers, "The document marks different correct answers with \"*\" and with the answer line.");
                return [];
            }
            else
            {
                correct = starred.Count > 0 ? starred : answered;
            }

            if (correct.Count == 0 && !unknownAnswer)
                Add(issues, ImportIssueCodes.NoCorrectAnswer, "No correct answer was found.");
            else if (correct.Count > 1)
                Add(issues, ImportIssueCodes.MultipleCorrectAnswers, "More than one answer is marked correct.");

            return correct;
        }

        // Option positions named by an answer value: one or several letters, or the exact text of one option.
        // A null entry means the value names no option of this question.
        private IEnumerable<int?> AnswerTargets(string value)
        {
            var normalized = value.Trim();
            var single = SingleLetterPattern().Match(normalized);
            if (single.Success)
                return [OptionByLetter(single.Groups["letter"].Value)];

            var several = SeveralLettersPattern().Match(normalized);
            if (several.Success)
            {
                var letters = several.Groups[1].Captures.Concat(several.Groups[2].Captures).Select(c => c.Value);
                return letters.Select(OptionByLetter).ToList();
            }

            var byText = Options.FindIndex(o => string.Equals(o.Text.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
            return [byText >= 0 ? byText : null];
        }

        private int? OptionByLetter(string letter)
        {
            var index = LetterIndex(letter);
            var position = Options.FindIndex(o => o.LetterIndex == index);
            return position >= 0 ? position : null;
        }

        private HashSet<int> NoAnswerForEssay(List<ImportIssue> issues)
        {
            if (AnswerValues.Count > 0)
                Add(issues, ImportIssueCodes.AnswerWithoutOptions, "The question has an answer line but no options. Use \"Model answer:\" for an essay question.");
            return [];
        }

        private void CheckLetterSequence(List<ImportIssue> issues)
        {
            for (var i = 0; i < Options.Count; i++)
            {
                var letter = Options[i].LetterIndex;
                if (i > 0 && letter <= Options[i - 1].LetterIndex)
                {
                    Add(issues, ImportIssueCodes.AmbiguousSplit, "The option letters start again; this may be two questions without a question number between them.");
                    return;
                }
                if (letter != i + 1)
                {
                    Add(issues, ImportIssueCodes.OptionLettersOutOfSequence, "The option letters are not in order (A, B, C, ...); an option may be missing.");
                    return;
                }
            }
        }

        private decimal? ResolveMarks(List<ImportIssue> issues)
        {
            if (MarksValues.Count == 0)
            {
                Add(issues, ImportIssueCodes.MarksMissing, "No marks were found for this question.");
                return null;
            }

            var parsed = MarksValues.Select(ParseMarks).ToList();
            if (parsed.Any(m => m is null) || parsed.Distinct().Count() > 1)
            {
                Add(issues, ImportIssueCodes.MarksInvalid, "The marks are not a single valid number.");
                return null;
            }

            var marks = parsed[0]!.Value;
            if (!QuestionRules.MarksAreValid(marks))
            {
                Add(issues, ImportIssueCodes.MarksInvalid,
                    string.Create(CultureInfo.InvariantCulture, $"Marks must be greater than 0, at most {QuestionRules.MaxMarks} and have at most {QuestionRules.MarksDecimalPlaces} decimal places."));
                return null;
            }
            return marks;
        }

        private static decimal? ParseMarks(string value) =>
            decimal.TryParse(NormalizeForMarks(value), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var marks)
                ? marks
                : null;

        private static string NormalizeForMarks(string text) => Line.From(text).Normalized;

        private static void Add(List<ImportIssue> issues, string code, string message)
        {
            if (!issues.Any(issue => issue.Code == code && issue.Message == message))
                issues.Add(new ImportIssue(code, message));
        }
    }
}
