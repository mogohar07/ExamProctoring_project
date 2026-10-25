using ExamProctoring.Application.Exams.Import;

namespace ExamProctoring.Application.Abstractions;

// Reads the text of a Word (.docx) document in reading order, for the exam import. Implementations work in memory,
// never store the document and never log its content.
public interface IDocxTextExtractor
{
    DocxReadResult Read(Stream document, CancellationToken cancellationToken);
}
