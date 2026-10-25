using ExamProctoring.Application.Exams.Import;

namespace ExamProctoring.UnitTests.Exams;

// Word import format (owner decision 2026-10-24): English and Arabic markers, and "report, never guess".
public sealed class QuestionImportParserTests
{
    private static DocxImportResponse Parse(params string[] lines) =>
        QuestionImportParser.Parse(lines.Select(DocxItem.Line).ToList(), maxMcqOptions: 8);

    private static DocxImportResponse Parse(int maxMcqOptions, params DocxItem[] items) =>
        QuestionImportParser.Parse(items, maxMcqOptions);

    private static string[] Codes(ImportedQuestion question) => question.Issues.Select(i => i.Code).ToArray();

    // ---- Complete questions

    [Fact]
    public void EnglishMcq_WithAnswerLineAndMarks_IsReady()
    {
        var result = Parse("1. What is the capital of France?", "A) Paris", "B) Lyon", "C) Nice", "Answer: A", "Marks: 2");

        var question = Assert.Single(result.Questions);
        Assert.Equal(ImportStatus.Ready, question.Status);
        Assert.Equal("Mcq", question.Type);
        Assert.Equal("What is the capital of France?", question.Text);
        Assert.Equal(2m, question.Marks);
        Assert.Null(question.ModelAnswer);
        Assert.Equal(["Paris", "Lyon", "Nice"], question.Options.Select(o => o.Text));
        Assert.Equal([true, false, false], question.Options.Select(o => o.IsCorrect));
        Assert.Empty(question.Issues);
        Assert.Equal(new ImportSummary(1, 1, 0, 0), result.Summary);
        Assert.Equal("1. What is the capital of France?\nA) Paris\nB) Lyon\nC) Nice\nAnswer: A\nMarks: 2", question.SourceText);
    }

    [Fact]
    public void StarBeforeTheLetter_MarksTheCorrectOption()
    {
        var question = Assert.Single(Parse("Q1. Pick one", "a. One", "*b. Two", "Marks: 1").Questions);

        Assert.Equal(ImportStatus.Ready, question.Status);
        Assert.Equal([false, true], question.Options.Select(o => o.IsCorrect));
    }

    [Theory]
    [InlineData("1) Text")]
    [InlineData("1- Text")]
    [InlineData("1: Text")]
    [InlineData("Q1. Text")]
    [InlineData("Question 1: Text")]
    [InlineData("question 12) Text")]
    [InlineData("1.Text")]
    [InlineData("س1 Text")]
    [InlineData("س1. Text")]
    [InlineData("سؤال 1: Text")]
    [InlineData("س١: Text")]
    public void QuestionStartForms_AreRecognized(string line)
    {
        var question = Assert.Single(Parse(line, "Marks: 1").Questions);

        Assert.Equal("Text", question.Text);
    }

    [Theory]
    [InlineData("A) Paris", "B) Lyon")]
    [InlineData("A. Paris", "B. Lyon")]
    [InlineData("(A) Paris", "(B) Lyon")]
    [InlineData("A - Paris", "B - Lyon")]
    [InlineData("a) Paris", "b) Lyon")]
    [InlineData("أ) Paris", "ب) Lyon")]
    [InlineData("أ- Paris", "ب- Lyon")]
    [InlineData("أ. Paris", "ب. Lyon")]
    public void OptionForms_AreRecognized(string first, string second)
    {
        var question = Assert.Single(Parse("1. Capital?", first, second, "Answer: A", "Marks: 1").Questions);
        Assert.Equal(["Paris", "Lyon"], question.Options.Select(o => o.Text));
    }

    [Fact]
    public void ArabicMcq_WithArabicMarkers_IsReady()
    {
        var question = Assert.Single(Parse("س1: ما عاصمة فرنسا؟", "أ) باريس", "ب) ليون", "الإجابة: أ", "الدرجة: ٢").Questions);

        Assert.Equal(ImportStatus.Ready, question.Status);
        Assert.Equal("ما عاصمة فرنسا؟", question.Text);
        Assert.Equal(["باريس", "ليون"], question.Options.Select(o => o.Text));
        Assert.Equal([true, false], question.Options.Select(o => o.IsCorrect));
        Assert.Equal(2m, question.Marks);
    }

    [Fact]
    public void ArabicAnswerLine_WithTheWordCorrect_IsRecognized()
    {
        var question = Assert.Single(Parse("1. Q", "أ) س", "ب) ص", "الإجابة الصحيحة: ب", "Marks: 1").Questions);

        Assert.Equal([false, true], question.Options.Select(o => o.IsCorrect));
    }

    [Fact]
    public void Essay_WithModelAnswer_IsReady_AndModelAnswerContinuesOverLines()
    {
        var question = Assert.Single(Parse("2. Explain the water cycle.", "Marks: 5", "Model answer: Evaporation,", "condensation, precipitation.").Questions);

        Assert.Equal(ImportStatus.Ready, question.Status);
        Assert.Equal("Essay", question.Type);
        Assert.Empty(question.Options);
        Assert.Equal("Evaporation,\ncondensation, precipitation.", question.ModelAnswer);
    }

    [Fact]
    public void ArabicModelAnswer_IsNotReadAsAnAnswerLine()
    {
        var question = Assert.Single(Parse("س1 اشرح الدورة", "الدرجة: 4", "الإجابة النموذجية: التبخر").Questions);

        Assert.Equal(ImportStatus.Ready, question.Status);
        Assert.Equal("التبخر", question.ModelAnswer);
    }

    [Fact]
    public void QuestionText_ContinuesOverLines_UntilTheOptions()
    {
        var question = Assert.Single(Parse("1. First line", "second line", "A) x", "B) y", "Answer: B", "Marks: 1").Questions);

        Assert.Equal("First line\nsecond line", question.Text);
    }

    [Theory]
    [InlineData("1. Question (2 marks)", 2)]
    [InlineData("1. Question (1.5 points)", 1.5)]
    [InlineData("1. Question (3 درجات)", 3)]
    public void InlineMarks_AreReadAndRemovedFromTheText(string line, decimal marks)
    {
        var question = Assert.Single(Parse(line).Questions);

        Assert.Equal("Question", question.Text);
        Assert.Equal(marks, question.Marks);
    }

    [Fact]
    public void LinesBeforeTheFirstQuestion_AreUnassigned()
    {
        var result = Parse("Midterm exam", "Answer all questions.", "1. Q", "Marks: 1");

        Assert.Equal(["Midterm exam", "Answer all questions."], result.UnassignedText);
    }

    [Fact]
    public void NoQuestions_GivesAnEmptyResult()
    {
        var result = Parse("Just some text", "and more");

        Assert.Empty(result.Questions);
        Assert.Equal(new ImportSummary(0, 0, 0, 0), result.Summary);
        Assert.Equal(2, result.UnassignedText.Count);
    }

    // ---- Report, never guess

    [Fact]
    public void NoCorrectAnswer_IsReported_AndNoOptionIsMarked()
    {
        var question = Assert.Single(Parse("1. Q", "A) x", "B) y", "Marks: 1").Questions);

        Assert.Equal(ImportStatus.NeedsAttention, question.Status);
        Assert.Contains("no_correct_answer", Codes(question));
        Assert.All(question.Options, o => Assert.False(o.IsCorrect));
    }

    [Fact]
    public void MissingMarks_AreReported_AndNotFilledIn()
    {
        var question = Assert.Single(Parse("1. Q", "A) x", "B) y", "Answer: A").Questions);

        Assert.Null(question.Marks);
        Assert.Equal(["marks_missing"], Codes(question));
    }

    [Theory]
    [InlineData("Marks: 0")]
    [InlineData("Marks: -1")]
    [InlineData("Marks: 1.005")]
    [InlineData("Marks: 10000")]
    [InlineData("Marks: two")]
    [InlineData("Marks: 2,5")]
    public void InvalidMarks_AreReported(string marksLine)
    {
        var question = Assert.Single(Parse("1. Q", "A) x", "B) y", "Answer: A", marksLine).Questions);

        Assert.Null(question.Marks);
        Assert.Contains("marks_invalid", Codes(question));
    }

    [Fact]
    public void ConflictingMarks_AreReported()
    {
        var question = Assert.Single(Parse("1. Q (2 marks)", "A) x", "B) y", "Answer: A", "Marks: 3").Questions);

        Assert.Null(question.Marks);
        Assert.Contains("marks_invalid", Codes(question));
    }

    [Fact]
    public void TwoStarredOptions_AreReported_AsMultipleCorrectAnswers()
    {
        var question = Assert.Single(Parse("1. Q", "*A) x", "*B) y", "Marks: 1").Questions);

        Assert.Contains("multiple_correct_answers", Codes(question));
    }

    [Theory]
    [InlineData("Answer: A, B")]
    [InlineData("Answer: A and B")]
    [InlineData("الإجابة: أ، ب")]
    public void SeveralLettersOnTheAnswerLine_AreReported(string answerLine)
    {
        var question = Assert.Single(Parse("1. Q", "A) x", "B) y", answerLine, "Marks: 1").Questions);

        Assert.Contains("multiple_correct_answers", Codes(question));
    }

    [Fact]
    public void AnswerLetterWithoutSuchOption_IsReported()
    {
        var question = Assert.Single(Parse("1. Q", "A) x", "B) y", "Answer: D", "Marks: 1").Questions);

        Assert.Contains("answer_not_matching_option", Codes(question));
        Assert.DoesNotContain("no_correct_answer", Codes(question));
        Assert.All(question.Options, o => Assert.False(o.IsCorrect));
    }

    [Fact]
    public void AnswerGivenAsTheExactOptionText_IsAccepted()
    {
        var question = Assert.Single(Parse("1. Q", "A) Paris", "B) Lyon", "Answer: lyon", "Marks: 1").Questions);

        Assert.Equal(ImportStatus.Ready, question.Status);
        Assert.Equal([false, true], question.Options.Select(o => o.IsCorrect));
    }

    [Fact]
    public void StarAndAnswerLineThatDisagree_AreReported_AndNothingIsMarked()
    {
        var question = Assert.Single(Parse("1. Q", "*A) x", "B) y", "Answer: B", "Marks: 1").Questions);

        Assert.Contains("conflicting_answer_markers", Codes(question));
        Assert.All(question.Options, o => Assert.False(o.IsCorrect));
    }

    [Fact]
    public void AnswerLineOnAnEssay_IsReported_AndNotUsed()
    {
        var question = Assert.Single(Parse("1. Explain.", "Answer: something", "Marks: 2").Questions);

        Assert.Equal("Essay", question.Type);
        Assert.Contains("answer_without_options", Codes(question));
        Assert.Null(question.ModelAnswer);
    }

    [Fact]
    public void OneOption_IsReported_AsTooFew()
    {
        var question = Assert.Single(Parse("1. Q", "*A) x", "Marks: 1").Questions);

        Assert.Contains("too_few_options", Codes(question));
    }

    [Fact]
    public void OptionsAboveTheConfiguredMaximum_AreReported()
    {
        var items = new[] { "1. Q", "*A) a", "B) b", "C) c", "D) d", "Marks: 1" }.Select(DocxItem.Line).ToArray();

        Assert.DoesNotContain("too_many_options", Codes(Parse(4, items).Questions[0]));
        Assert.Contains("too_many_options", Codes(Parse(3, items).Questions[0]));
    }

    [Fact]
    public void RepeatedOptionText_IsReported()
    {
        var question = Assert.Single(Parse("1. Q", "*A) Paris", "B) paris ", "Marks: 1").Questions);

        Assert.Contains("duplicate_option_text", Codes(question));
    }

    [Fact]
    public void EmptyOption_IsReported()
    {
        var question = Assert.Single(Parse("1. Q", "*A) x", "B)", "Marks: 1").Questions);

        Assert.Contains("option_text_missing", Codes(question));
    }

    [Fact]
    public void SkippedOptionLetter_IsReported()
    {
        var question = Assert.Single(Parse("1. Q", "*A) x", "B) y", "D) z", "Marks: 1").Questions);

        Assert.Contains("option_letters_out_of_sequence", Codes(question));
    }

    [Fact]
    public void OptionLettersStartingAgain_AreReported_AsAnAmbiguousSplit()
    {
        var question = Assert.Single(Parse("1. Q", "*A) x", "B) y", "A) z", "B) w", "Marks: 1").Questions);

        Assert.Contains("ambiguous_split", Codes(question));
    }

    [Fact]
    public void TooLongTexts_AreReported()
    {
        var question = Assert.Single(Parse(
            "1. " + new string('q', 4001), "*A) " + new string('o', 1001), "B) y", "Marks: 1",
            "Model answer: " + new string('m', 4001)).Questions);

        Assert.Contains("text_too_long", Codes(question));
        Assert.Contains("option_too_long", Codes(question));
        Assert.Contains("model_answer_too_long", Codes(question));
    }

    [Fact]
    public void QuestionNumberWithoutTextOrOptions_IsNotImported()
    {
        var question = Assert.Single(Parse("5.").Questions);

        Assert.Equal(ImportStatus.NotImported, question.Status);
        Assert.Null(question.Type);
        Assert.Contains("text_missing", Codes(question));
    }

    [Fact]
    public void OptionsWithoutQuestionText_NeedAttention()
    {
        var question = Assert.Single(Parse("1.", "*A) x", "B) y", "Marks: 1").Questions);

        Assert.Equal(ImportStatus.NeedsAttention, question.Status);
        Assert.Contains("text_missing", Codes(question));
    }

    [Fact]
    public void UnknownLinesAfterTheOptions_AreUnassigned_AndReported()
    {
        var result = Parse("1. Q", "*A) x", "B) y", "some stray line", "Marks: 1");

        Assert.Equal(["some stray line"], result.UnassignedText);
        Assert.Contains("unrecognized_lines", Codes(result.Questions[0]));
    }

    [Fact]
    public void UnsupportedContent_IsAttachedToItsQuestion_AndItsTextKept()
    {
        var result = Parse(8,
            DocxItem.UnsupportedContent(UnsupportedContentKind.Image),
            DocxItem.Line("1. Q"),
            DocxItem.UnsupportedContent(UnsupportedContentKind.Table, ["cell 1 | cell 2"]),
            DocxItem.Line("*A) x"),
            DocxItem.Line("B) y"),
            DocxItem.Line("Marks: 1"));

        Assert.Equal([new UnsupportedContentItem(null, "Image"), new UnsupportedContentItem(1, "Table")], result.UnsupportedContent);
        Assert.Equal(["cell 1 | cell 2"], result.UnassignedText);
        Assert.Equal(ImportStatus.NeedsAttention, result.Questions[0].Status);
        Assert.Contains("unsupported_content", Codes(result.Questions[0]));
    }

    [Fact]
    public void SeveralQuestions_AreNumberedInOrder_AndSummarized()
    {
        var result = Parse(
            "1. Q1", "*A) a", "B) b", "Marks: 1",
            "2. Q2", "A) a", "B) b", "Marks: 1",
            "3.");

        Assert.Equal([1, 2, 3], result.Questions.Select(q => q.Index));
        Assert.Equal(new ImportSummary(3, 1, 1, 1), result.Summary);
    }

    [Fact]
    public void BidiControlCharacters_DoNotHideMarkers()
    {
        var question = Assert.Single(Parse("‏س1: سؤال", "‏أ) نعم", "‏ب) لا", "‏الإجابة: أ", "‏الدرجة: 1").Questions);

        Assert.Equal(ImportStatus.Ready, question.Status);
    }

    [Fact]
    public void ArabicIndicDigits_InQuestionText_AreKeptAsWritten()
    {
        var question = Assert.Single(Parse("س١: كم يساوي ٢ + ٢؟", "الدرجة: ١").Questions);

        Assert.Equal("كم يساوي ٢ + ٢؟", question.Text);
        Assert.Equal(1m, question.Marks);
    }

    // ---- Question splitting (audit 2026-10-25)

    [Fact]
    public void NumberedLinesInAModelAnswer_StayInTheModelAnswer()
    {
        var result = Parse(
            "1. Explain the water cycle.", "Marks: 5", "Model answer:",
            "1. Evaporation", "2. Condensation", "3. Precipitation",
            "2. Next question?", "Marks: 2");

        Assert.Equal(2, result.Questions.Count);
        var first = result.Questions[0];
        Assert.Equal(ImportStatus.Ready, first.Status);
        Assert.Equal("Explain the water cycle.", first.Text);
        Assert.Equal("1. Evaporation\n2. Condensation\n3. Precipitation", first.ModelAnswer);
        Assert.Equal(5m, first.Marks);
        var second = result.Questions[1];
        Assert.Equal(ImportStatus.Ready, second.Status);
        Assert.Equal("Next question?", second.Text);
        Assert.Equal(2m, second.Marks);
        Assert.Empty(result.UnassignedText);
    }

    [Fact]
    public void NumberedLinesInAnArabicModelAnswer_StayInTheModelAnswer()
    {
        var result = Parse(
            "س1: اشرح دورة الماء.", "الدرجة: 5", "الإجابة النموذجية: المراحل:",
            "١. التبخر", "٢. التكاثف",
            "س2: سؤال تالٍ؟", "الدرجة: 2");

        Assert.Equal(2, result.Questions.Count);
        Assert.Equal(ImportStatus.Ready, result.Questions[0].Status);
        Assert.Equal("المراحل:\n١. التبخر\n٢. التكاثف", result.Questions[0].ModelAnswer);
        Assert.Equal("سؤال تالٍ؟", result.Questions[1].Text);
    }

    [Fact]
    public void ANumberedLineFollowedByMarks_StartsTheNextQuestion_EvenWhenItContinuesTheList()
    {
        // "2." continues the model answer's list and is also the next question; its own marks decide.
        var result = Parse(
            "1. Explain.", "Marks: 5", "Model answer: Steps:", "1. Evaporation",
            "2. Name a gas.", "Marks: 1");

        Assert.Equal(2, result.Questions.Count);
        Assert.Equal("Steps:\n1. Evaporation", result.Questions[0].ModelAnswer);
        Assert.Equal("Name a gas.", result.Questions[1].Text);
        Assert.Equal(1m, result.Questions[1].Marks);
        Assert.All(result.Questions, question => Assert.Equal(ImportStatus.Ready, question.Status));
    }

    [Theory]
    [InlineData("*A) a")]
    [InlineData("Answer: A")]
    [InlineData("Marks: 1")]
    [InlineData("Model answer: y")]
    public void ANumberedLineFollowedByQuestionLines_StartsANewQuestion(string questionLine)
    {
        // A document that numbers every question "1." must not hide a question inside the previous model answer.
        var result = Parse("1. Explain.", "Marks: 5", "Model answer: x", "1. Next question?", questionLine);

        Assert.Equal(2, result.Questions.Count);
        Assert.Equal("x", result.Questions[0].ModelAnswer);
        Assert.Equal(ImportStatus.Ready, result.Questions[0].Status);
        Assert.Equal("Next question?", result.Questions[1].Text);
    }

    [Fact]
    public void ANumberedLineAfterAModelAnswerWithoutAList_StartsTheNextQuestion()
    {
        var result = Parse("1. Explain.", "Marks: 5", "Model answer: Because.", "2. Next?", "Marks: 1");

        Assert.Equal(["Explain.", "Next?"], result.Questions.Select(q => q.Text));
        Assert.Equal("Because.", result.Questions[0].ModelAnswer);
        Assert.All(result.Questions, question => Assert.Equal(ImportStatus.Ready, question.Status));
    }

    [Fact]
    public void MarksAfterANumberedModelAnswer_WhenTheQuestionHasNoMarks_AreReportedOnBothReadings()
    {
        // "2. b" is either the model answer's second step (marks closing the question) or a new question with marks.
        var result = Parse("1. Explain.", "Model answer:", "1. a", "2. b", "Marks: 5");

        Assert.Equal(2, result.Questions.Count);
        Assert.Equal("1. a", result.Questions[0].ModelAnswer);
        Assert.All(result.Questions, question =>
        {
            Assert.Equal(ImportStatus.NeedsAttention, question.Status);
            Assert.Contains(ImportIssueCodes.AmbiguousSplit, Codes(question));
        });
    }

    [Fact]
    public void MarksBeforeANumberedModelAnswer_KeepTheWholeListInTheModelAnswer()
    {
        var result = Parse("1. Explain.", "Marks: 5", "Model answer:", "1. a", "2. b", "2. Next?", "Marks: 1");

        Assert.Equal(["1. a\n2. b", null], result.Questions.Select(q => q.ModelAnswer));
        Assert.All(result.Questions, question => Assert.Equal(ImportStatus.Ready, question.Status));
    }

    // Final review probe B: three valid questions with inline marks disappeared into one Ready model answer.
    [Fact]
    public void QuestionsWithInlineMarks_AfterANumberedModelAnswer_AreNotAbsorbed()
    {
        var result = Parse(
            "1. Explain the water cycle.", "Marks: 5", "Model answer:", "1. Evaporation",
            "2. Define condensation. (2 marks)", "3. Define precipitation. (3 marks)", "4. Name one greenhouse gas. (1 mark)");

        Assert.Equal(4, result.Summary.Found);
        Assert.Equal(["Explain the water cycle.", "Define condensation.", "Define precipitation.", "Name one greenhouse gas."],
            result.Questions.Select(q => q.Text));
        Assert.Equal([5m, 2m, 3m, 1m], result.Questions.Select(q => q.Marks));
        Assert.Equal("1. Evaporation", result.Questions[0].ModelAnswer);

        // Lines 2-4 could also be marking-scheme points of question 1 ("2. Condensation (2 marks)"), so every
        // question is reported for review instead of being Ready.
        Assert.All(result.Questions, question =>
        {
            Assert.Equal(ImportStatus.NeedsAttention, question.Status);
            Assert.Contains(ImportIssueCodes.AmbiguousSplit, Codes(question));
        });
    }

    // Final review probe C: the same with Arabic inline marks.
    [Fact]
    public void QuestionsWithArabicInlineMarks_AfterANumberedModelAnswer_AreNotAbsorbed()
    {
        var result = Parse("س1: اشرح دورة الماء.", "الدرجة: 5", "الإجابة النموذجية:", "1. التبخر", "2. عرّف التكاثف. (2 درجة)");

        Assert.Equal(2, result.Summary.Found);
        Assert.Equal("1. التبخر", result.Questions[0].ModelAnswer);
        Assert.Equal("عرّف التكاثف.", result.Questions[1].Text);
        Assert.Equal(2m, result.Questions[1].Marks);
        Assert.All(result.Questions, question => Assert.Contains(ImportIssueCodes.AmbiguousSplit, Codes(question)));
        Assert.Equal(0, result.Summary.Ready);
    }

    // Final review probe A: a numbered line with the next question's number, and no such question after it.
    [Fact]
    public void ALineWithTheNextQuestionNumber_KeptInTheModelAnswer_IsReported_WhenNoSuchQuestionFollows()
    {
        var result = Parse("1. Explain the water cycle.", "Marks: 5", "Model answer: Steps:", "1. Evaporation", "2. Name the gas that forms clouds.");

        var question = Assert.Single(result.Questions);
        Assert.Equal("Steps:\n1. Evaporation\n2. Name the gas that forms clouds.", question.ModelAnswer);
        Assert.Equal(ImportStatus.NeedsAttention, question.Status);
        Assert.Contains(ImportIssueCodes.AmbiguousSplit, Codes(question));
    }

    [Fact]
    public void ALineWithTheNextQuestionNumber_IsConfirmedAsAListLine_WhenThatQuestionFollows()
    {
        var result = Parse(
            "1. Explain.", "Marks: 5", "Model answer:", "1. a", "2. b", "3. c",
            "2. Next question?", "Marks: 1");

        Assert.Equal(2, result.Questions.Count);
        Assert.Equal("1. a\n2. b\n3. c", result.Questions[0].ModelAnswer);
        Assert.All(result.Questions, question => Assert.Equal(ImportStatus.Ready, question.Status));
    }

    [Fact]
    public void AMarkingSchemeWithInlineMarks_InAModelAnswer_IsReported_NotSplitSilently()
    {
        var result = Parse("1. Explain.", "Marks: 4", "Model answer:", "1. Evaporation (2 marks)", "2. Condensation (2 marks)");

        Assert.NotEmpty(result.Questions);
        Assert.Equal(0, result.Summary.Ready);
        Assert.All(result.Questions, question => Assert.Contains(ImportIssueCodes.AmbiguousSplit, Codes(question)));
    }

    [Fact]
    public void TheDoubtAfterAModelAnswer_EndsAtTheFirstCompleteQuestion()
    {
        var result = Parse(
            "1. Explain.", "Marks: 4", "Model answer:", "1. a",
            "2. Define b. (2 marks)", "3. Pick one", "*A) x", "B) y", "Marks: 1", "4. Define c. (1 mark)");

        Assert.Equal(4, result.Questions.Count);
        Assert.Equal([ImportStatus.NeedsAttention, ImportStatus.NeedsAttention, ImportStatus.NeedsAttention, ImportStatus.Ready],
            result.Questions.Select(q => q.Status));
    }

    [Theory]
    [InlineData("Q2. Next question?")]
    [InlineData("Question 2: Next question?")]
    [InlineData("س2: سؤال تالٍ؟")]
    public void AQuestionMarker_InsideAModelAnswer_AlwaysStartsAQuestion(string line)
    {
        var result = Parse("1. Explain.", "Marks: 5", "Model answer:", "1. a", line);

        Assert.Equal(2, result.Questions.Count);
        Assert.Equal("1. a", result.Questions[0].ModelAnswer);
        Assert.Equal(ImportStatus.Ready, result.Questions[0].Status);
    }

    [Theory]
    [InlineData("2.5 kg. What is its mass in grams?")]
    [InlineData("12-10-2026 is the deadline. Which day is it?")]
    [InlineData("10:30 is the start time. How long is the exam?")]
    [InlineData("3.14 is close to which number?")]
    [InlineData("٢٫٥ كجم. ما الكتلة بالغرام؟")]
    public void DecimalsDatesAndTimes_AtTheStartOfALine_AreQuestionText(string line)
    {
        var question = Assert.Single(Parse("1. A box weighs", line, "Marks: 1").Questions);

        Assert.Equal(ImportStatus.Ready, question.Status);
        Assert.Equal("A box weighs\n" + line, question.Text);
    }

    [Fact]
    public void ADecimalInsideAModelAnswer_StaysInTheModelAnswer()
    {
        var question = Assert.Single(Parse("1. Give pi.", "Marks: 1", "Model answer:", "3.14 approximately").Questions);

        Assert.Equal("3.14 approximately", question.ModelAnswer);
    }
}
