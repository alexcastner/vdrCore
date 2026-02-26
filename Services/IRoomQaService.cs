using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public interface IRoomQaService
    {
        Task<RoomQuestion> AskQuestionAsync(Guid tenantId, Guid roomId, string subject, string body, string userId, string? email, CancellationToken ct = default);
        Task<RoomAnswer> AnswerQuestionAsync(Guid tenantId, int questionId, string body, string userId, string? email, CancellationToken ct = default);
        Task CloseQuestionAsync(Guid tenantId, int questionId, CancellationToken ct = default);
        Task<List<RoomQuestion>> ListQuestionsAsync(Guid tenantId, Guid roomId, CancellationToken ct = default);
        Task<RoomQuestion?> GetQuestionAsync(Guid tenantId, int questionId, CancellationToken ct = default);
        Task<List<RoomAnswer>> ListAnswersAsync(Guid tenantId, int questionId, CancellationToken ct = default);
    }
}
