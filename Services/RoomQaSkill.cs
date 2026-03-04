using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;

namespace twoSaaSCore.Services
{
    /// <summary>Semantic Kernel plugin for creating room Q&A questions from chat.</summary>
    public sealed class RoomQaSkill
    {
        private readonly IRoomQaService _qaService;
        private readonly Guid _tenantId;
        private readonly Guid _roomId;
        private readonly string _userId;
        private readonly string? _userEmail;

        public RoomQaSkill(IRoomQaService qaService, Guid tenantId, Guid roomId, string userId, string? userEmail)
        {
            _qaService = qaService;
            _tenantId = tenantId;
            _roomId = roomId;
            _userId = userId;
            _userEmail = userEmail;
        }

        /// <summary>Creates a Q&A question entry in the current room.</summary>
        [KernelFunction("submit_room_question")]
        [Description("Create a new question in the room Q&A section. Use this when a user asks to save or post their question to Q&A.")]
        public async Task<string> SubmitRoomQuestionAsync(
            [Description("Short subject/title for the Q&A entry")] string subject,
            [Description("Detailed question body to store in Q&A")] string question,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                subject = "Question from AI chat";
            }

            if (string.IsNullOrWhiteSpace(question))
            {
                return "Cannot create a Q&A entry because the question text was empty.";
            }

            var created = await _qaService.AskQuestionAsync(
                _tenantId,
                _roomId,
                subject.Trim(),
                question.Trim(),
                _userId,
                _userEmail,
                ct);

            return $"Created Q&A question #{created.Id} with subject '{created.Subject}'.";
        }
    }
}
