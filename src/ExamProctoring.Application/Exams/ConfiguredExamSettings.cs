using ExamProctoring.Application.Abstractions;
using ExamProctoring.Application.Common;
using Microsoft.Extensions.Options;

namespace ExamProctoring.Application.Exams;

// IExamSettings from the "Exams" configuration section (D26), until Admin-managed settings exist.
public sealed class ConfiguredExamSettings(IOptions<ExamOptions> options) : IExamSettings
{
    public int WarningLimit => options.Value.WarningLimit;

    public int MaxMcqOptions => options.Value.MaxMcqOptions;
}
