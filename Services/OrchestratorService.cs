using WebCodeWorkExecutor.Dtos;

namespace WebCodeWorkExecutor.Services
{
    public interface IOrchestratorService
    {
        Task<bool> SendEvaluation(BatchExecuteResponse evaluation);
    }

    public class OrchestratorService : IOrchestratorService
    {
        private readonly ILogger<OrchestratorService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        private const string SEND_EVAL_RESULTS_ENDPOINT = "/api/evaluate/container-submit";

        public OrchestratorService(
            ILogger<OrchestratorService> loggerFactory,
            IHttpClientFactory httpClientFactory
        )
        {
            _logger = loggerFactory;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<bool> SendEvaluation(BatchExecuteResponse evaluation)
        {
            var httpClient = _httpClientFactory.CreateClient("OrchestratorClient");
            var response = await httpClient.PostAsJsonAsync(SEND_EVAL_RESULTS_ENDPOINT, evaluation);

            _logger.LogInformation($"Request returned: {response.StatusCode} {response.ReasonPhrase} : {await response.Content.ReadAsStringAsync()}");

            if (response.IsSuccessStatusCode)
                return true;
            _logger.LogWarning("Submitting from container to orchestrator did not work");
            return false;
        }
    }
}