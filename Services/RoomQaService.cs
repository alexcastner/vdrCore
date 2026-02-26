using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using twoSaaSCore.Data;
using twoSaaSCore.Models;

namespace twoSaaSCore.Services
{
    public class RoomQaService : IRoomQaService
    {
        private readonly ApplicationDbContext _db;

        public RoomQaService(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<RoomQuestion> AskQuestionAsync(Guid tenantId, Guid roomId, string subject, string body, string userId, string? email, CancellationToken ct = default)
        {
            var q = new RoomQuestion
            {
                TenantId = tenantId,
                RoomId = roomId,
                Subject = subject,
                Body = body,
                AskedByUserId = userId,
                AskedByEmail = email,
                AskedUtc = DateTimeOffset.UtcNow,
                Status = QuestionStatus.Open
            };
            _db.RoomQuestions.Add(q);
            await _db.SaveChangesAsync(ct);
            return q;
        }

        public async Task<RoomAnswer> AnswerQuestionAsync(Guid tenantId, int questionId, string body, string userId, string? email, CancellationToken ct = default)
        {
            var question = await _db.RoomQuestions.FirstOrDefaultAsync(q => q.Id == questionId, ct);
            if (question == null) throw new InvalidOperationException("Question not found.");

            var a = new RoomAnswer
            {
                TenantId = tenantId,
                QuestionId = questionId,
                Body = body,
                AnsweredByUserId = userId,
                AnsweredByEmail = email,
                AnsweredUtc = DateTimeOffset.UtcNow
            };
            _db.RoomAnswers.Add(a);

            if (question.Status == QuestionStatus.Open)
                question.Status = QuestionStatus.Answered;

            await _db.SaveChangesAsync(ct);
            return a;
        }

        public async Task CloseQuestionAsync(Guid tenantId, int questionId, CancellationToken ct = default)
        {
            var q = await _db.RoomQuestions.FirstOrDefaultAsync(q => q.Id == questionId, ct);
            if (q != null)
            {
                q.Status = QuestionStatus.Closed;
                await _db.SaveChangesAsync(ct);
            }
        }

        public async Task<List<RoomQuestion>> ListQuestionsAsync(Guid tenantId, Guid roomId, CancellationToken ct = default)
        {
            return await _db.RoomQuestions
                .Where(q => q.RoomId == roomId)
                .OrderByDescending(q => q.AskedUtc)
                .ToListAsync(ct);
        }

        public async Task<RoomQuestion?> GetQuestionAsync(Guid tenantId, int questionId, CancellationToken ct = default)
        {
            return await _db.RoomQuestions.FirstOrDefaultAsync(q => q.Id == questionId, ct);
        }

        public async Task<List<RoomAnswer>> ListAnswersAsync(Guid tenantId, int questionId, CancellationToken ct = default)
        {
            return await _db.RoomAnswers
                .Where(a => a.QuestionId == questionId)
                .OrderBy(a => a.AnsweredUtc)
                .ToListAsync(ct);
        }
    }
}
