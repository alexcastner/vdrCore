using System.Threading.Tasks;
using Twilio;
using Twilio.Rest.Api.V2010.Account;

namespace twoSaaSCore.Services
{
    public class TwilioSmsSender : ISmsSender
    {
        private readonly string _from;

        public TwilioSmsSender(string sid, string token, string fromNumber)
        {
            TwilioClient.Init(sid, token);
            _from = fromNumber;
        }

        public async Task SendAsync(string toNumber, string message)
        {
            if (string.IsNullOrWhiteSpace(toNumber)) return;
            await MessageResource.CreateAsync(
                to: toNumber,
                from: _from,
                body: message);
        }
    }
}