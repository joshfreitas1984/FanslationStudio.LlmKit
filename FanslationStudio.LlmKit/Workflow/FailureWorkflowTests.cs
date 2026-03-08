//using FanslationStudio.LlmKit.Support;
//using FanslationStudio.LlmKit.Utility;
//using System.Text.RegularExpressions;

//namespace FanslationStudio.LlmKit.Tests
//{
//    public class FailureWorkflowTests
//    {
//        public static TextFileToSplit DefaultTestTextFile() => new TextFileToSplit()
//        {
//            Path = "",
//        };

//        public record FailedTranslation(string Text, string Translated, string Reason);

//        public async Task FindAllFailingTranslations()
//        {

//            var failures = new List<FailedTranslation>();
//            var pattern = LineValidation.ChineseCharPattern;

//            var forTheGlossary = new List<string>();

//            await FileIteration.IterateTranslatedFilesAsync(workingDirectory, async (outputFile, textFileToTranslate, fileLines) =>
//            {
//                foreach (var line in fileLines)
//                {
//                    foreach (var split in line.Splits)
//                    {
//                        if (string.IsNullOrEmpty(split.Text))
//                            continue;

//                        // If it is already translated or just special characters return it
//                        if (!Regex.IsMatch(split.Text, pattern))
//                            continue;

//                        if (!string.IsNullOrEmpty(split.Text) && (string.IsNullOrEmpty(split.Translated) || split.FlaggedForRetranslation))
//                        {
//                            failures.Add(new FailedTranslation(split.Text, split.Translated, split.FlaggedMistranslation));

//                            if (split.Text.Length < 6)
//                                if (!forTheGlossary.Contains(split.Text))
//                                    forTheGlossary.Add(split.Text);
//                        }
//                    }
//                }

//                await Task.CompletedTask;
//            });
//        }

//        public async Task TestExplainTagStripping()
//        {
//            using var client = new HttpClient();
//            client.Timeout = TimeSpan.FromSeconds(300);
//            var config = Configuration.GetConfiguration(workingDirectory);

//            var serializer = Yaml.CreateDeserializer();
//            var content = File.ReadAllText(FailingTransactionsPath);
//            var failures = serializer.Deserialize<List<FailedTranslation>>(content);

//            foreach (var failure in failures)
//            {
//                var textFile = DefaultTestTextFile();
//                textFile.EnableBasePrompts = true;
//                textFile.EnableGlossary = true;

//                var messages = TranslationService.GenerateBaseMessages(config, failure.Text, textFile);
//                messages.Add(LlmHelpers.GenerateAssistantPrompt(failure.Translated));
//                messages.Add(LlmHelpers.GenerateUserPrompt(
//                    @"You have removed a tag from translated text
//Can you update the current system prompt and give me the full system prompt that would stop it from happening in future?"));

//                var result = await TranslationService.TranslateMessagesAsync(client, config, messages);

//                File.WriteAllText($"{workingDirectory}/TestResults/Failed/TestExplain.txt", result);

//                return;
//            }
//        }

//        public async Task RetestNewSystemPrompts()
//        {
//            bool isManual = false;

//            using var client = new HttpClient();
//            client.Timeout = TimeSpan.FromSeconds(300);
//            var config = Configuration.GetConfiguration(workingDirectory);

//            var serializer = Yaml.CreateDeserializer();
//            var content = File.ReadAllText(FailingTransactionsPath);
//            var failures = serializer.Deserialize<List<FailedTranslation>>(content);

//            foreach (var failure in failures)
//            {
//                //if (Regex.IsMatch(failure.Translated, LineValidation.ChineseCharPattern))
//                {
//                    var textFile = DefaultTestTextFile();
//                    textFile.EnableGlossary = false;

//                    if (isManual)
//                    {
//                        var messages = TranslationService.GenerateBaseMessages(config, failure.Text, textFile);

//                        var result = await TranslationService.TranslateMessagesAsync(client, config, messages);
//                        File.WriteAllText($"{workingDirectory}/TestResults/Failed/RetestNewSystemPrompts.txt", result);


//                        if (Regex.IsMatch(result, LineValidation.ChineseCharPattern))
//                            Assert.Fail("The new system prompt did not work, it is still adding Chinese characters");
//                    }
//                    else
//                    {
//                        var result = await TranslationService.TranslateSplitAsync(config, failure.Text, client, DefaultTestTextFile());
//                        File.WriteAllText($"{workingDirectory}/TestResults/Failed/RetestNewSystemPrompts.txt", result.Result);
//                    }

//                    return;
//                }
//            }
//        }
//    }
//}
