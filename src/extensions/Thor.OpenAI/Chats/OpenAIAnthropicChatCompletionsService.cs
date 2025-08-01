﻿using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Thor.Abstractions;
using Thor.Abstractions.Anthropic;
using Thor.Abstractions.Chats;
using Thor.Abstractions.Chats.Dtos;
using Thor.Abstractions.Dtos;

namespace Thor.OpenAI.Chats;

/// <summary>
/// OpenAI到Claude适配器服务
/// 将Claude格式的请求转换为OpenAI格式，然后将OpenAI的响应转换为Claude格式
/// </summary>
public class OpenAIAnthropicChatCompletionsService : IAnthropicChatCompletionsService
{
    private readonly IThorChatCompletionsService _openAIChatService;
    private readonly ILogger<OpenAIAnthropicChatCompletionsService> _logger;

    public OpenAIAnthropicChatCompletionsService(
        IThorChatCompletionsService openAIChatService,
        ILogger<OpenAIAnthropicChatCompletionsService> logger)
    {
        _openAIChatService = openAIChatService;
        _logger = logger;
    }

    /// <summary>
    /// 非流式对话补全
    /// </summary>
    public async Task<ClaudeChatCompletionDto> ChatCompletionsAsync(AnthropicInput request,
        ThorPlatformOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 转换请求格式：Claude -> OpenAI
            var openAIRequest = ConvertAnthropicToOpenAI(request);

            // 调用OpenAI服务
            var openAIResponse =
                await _openAIChatService.ChatCompletionsAsync(openAIRequest, options, cancellationToken);

            // 转换响应格式：OpenAI -> Claude
            var claudeResponse = ConvertOpenAIToClaude(openAIResponse, request);

            return claudeResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI到Claude适配器异常");
            throw;
        }
    }

    /// <summary>
    /// 流式对话补全
    /// </summary>
    public async IAsyncEnumerable<(string?, ClaudeStreamDto?)> StreamChatCompletionsAsync(AnthropicInput request,
        ThorPlatformOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var openAIRequest = ConvertAnthropicToOpenAI(request);
        openAIRequest.Stream = true;

        var messageId = Guid.NewGuid().ToString();
        var hasStarted = false;
        var hasTextContentBlockStarted = false;
        var hasThinkingContentBlockStarted = false;
        var toolBlocksStarted = new Dictionary<int, bool>(); // 使用索引而不是ID
        var toolCallIds = new Dictionary<int, string>(); // 存储每个索引对应的ID
        var toolCallIndexToBlockIndex = new Dictionary<int, int>(); // 工具调用索引到块索引的映射
        var accumulatedUsage = new ClaudeChatCompletionDtoUsage();
        var isFinished = false;
        var currentContentBlockType = ""; // 跟踪当前内容块类型
        var currentBlockIndex = 0; // 跟踪当前块索引
        var lastContentBlockType = ""; // 跟踪最后一个内容块类型，用于确定停止原因

        await foreach (var openAIResponse in _openAIChatService.StreamChatCompletionsAsync(openAIRequest, options,
                           cancellationToken))
        {
            // 发送message_start事件
            if (!hasStarted && openAIResponse.Choices?.Count > 0 &&
                openAIResponse.Choices.Any(x => x.Delta.ToolCalls?.Count > 0) == false)
            {
                hasStarted = true;
                var messageStartEvent = CreateMessageStartEvent(messageId, request.Model);
                yield return ("message_start", messageStartEvent);
            }

            if (openAIResponse.Choices is { Count: > 0 })
            {
                var choice = openAIResponse.Choices.First();

                // 处理内容
                if (!string.IsNullOrEmpty(choice.Delta?.Content))
                {
                    // 如果当前有其他类型的内容块在运行，先结束它们
                    if (currentContentBlockType != "text" && !string.IsNullOrEmpty(currentContentBlockType))
                    {
                        var stopEvent = CreateContentBlockStopEvent();
                        stopEvent.index = currentBlockIndex;
                        yield return ("content_block_stop", stopEvent);
                        currentBlockIndex++; // 切换内容块时增加索引
                        currentContentBlockType = "";
                    }

                    // 发送content_block_start事件（仅第一次）
                    if (!hasTextContentBlockStarted || currentContentBlockType != "text")
                    {
                        hasTextContentBlockStarted = true;
                        currentContentBlockType = "text";
                        lastContentBlockType = "text";
                        var contentBlockStartEvent = CreateContentBlockStartEvent();
                        contentBlockStartEvent.index = currentBlockIndex;
                        yield return ("content_block_start", contentBlockStartEvent);
                    }

                    // 发送content_block_delta事件
                    var contentDeltaEvent = CreateContentBlockDeltaEvent(choice.Delta.Content);
                    contentDeltaEvent.index = currentBlockIndex;
                    yield return ("content_block_delta", contentDeltaEvent);
                }

                // 处理工具调用
                if (choice.Delta?.ToolCalls is { Count: > 0 })
                {
                    foreach (var toolCall in choice.Delta.ToolCalls)
                    {
                        var toolCallIndex = toolCall.Index; // 使用索引来标识工具调用

                        // 发送tool_use content_block_start事件
                        if (toolBlocksStarted.TryAdd(toolCallIndex, true))
                        {
                            // 如果当前有文本或thinking内容块在运行，先结束它们
                            if (currentContentBlockType == "text" || currentContentBlockType == "thinking")
                            {
                                var stopEvent = CreateContentBlockStopEvent();
                                stopEvent.index = currentBlockIndex;
                                yield return ("content_block_stop", stopEvent);
                                currentBlockIndex++; // 增加块索引
                            }
                            // 如果当前有其他工具调用在运行，也需要结束它们
                            else if (currentContentBlockType == "tool_use")
                            {
                                var stopEvent = CreateContentBlockStopEvent();
                                stopEvent.index = currentBlockIndex;
                                yield return ("content_block_stop", stopEvent);
                                currentBlockIndex++; // 增加块索引
                            }

                            currentContentBlockType = "tool_use";
                            lastContentBlockType = "tool_use";

                            // 为此工具调用分配一个新的块索引
                            toolCallIndexToBlockIndex[toolCallIndex] = currentBlockIndex;

                            // 保存工具调用的ID（如果有的话）
                            if (!string.IsNullOrEmpty(toolCall.Id))
                            {
                                toolCallIds[toolCallIndex] = toolCall.Id;
                            }
                            else if (!toolCallIds.ContainsKey(toolCallIndex))
                            {
                                // 如果没有ID且之前也没有保存过，生成一个新的ID
                                toolCallIds[toolCallIndex] = Guid.NewGuid().ToString();
                            }

                            var toolBlockStartEvent = CreateToolBlockStartEvent(
                                toolCallIds[toolCallIndex],
                                toolCall.Function?.Name);
                            toolBlockStartEvent.index = currentBlockIndex;
                            yield return ("content_block_start", toolBlockStartEvent);
                        }

                        // 如果有增量的参数，发送content_block_delta事件
                        if (!string.IsNullOrEmpty(toolCall.Function?.Arguments))
                        {
                            var toolDeltaEvent = CreateToolBlockDeltaEvent(toolCall.Function.Arguments);
                            // 使用该工具调用对应的块索引
                            toolDeltaEvent.index = toolCallIndexToBlockIndex[toolCallIndex];
                            yield return ("content_block_delta", toolDeltaEvent);
                        }
                    }
                }

                // 处理推理内容
                if (!string.IsNullOrEmpty(choice.Delta?.ReasoningContent))
                {
                    // 如果当前有其他类型的内容块在运行，先结束它们
                    if (currentContentBlockType != "thinking" && !string.IsNullOrEmpty(currentContentBlockType))
                    {
                        var stopEvent = CreateContentBlockStopEvent();
                        stopEvent.index = currentBlockIndex;
                        yield return ("content_block_stop", stopEvent);
                        currentBlockIndex++; // 增加块索引
                        currentContentBlockType = "";
                    }

                    // 对于推理内容，也需要发送对应的事件
                    if (!hasThinkingContentBlockStarted || currentContentBlockType != "thinking")
                    {
                        hasThinkingContentBlockStarted = true;
                        currentContentBlockType = "thinking";
                        lastContentBlockType = "thinking";
                        var thinkingBlockStartEvent = CreateThinkingBlockStartEvent();
                        thinkingBlockStartEvent.index = currentBlockIndex;
                        yield return ("content_block_start", thinkingBlockStartEvent);
                    }

                    var thinkingDeltaEvent = CreateThinkingBlockDeltaEvent(choice.Delta.ReasoningContent);
                    thinkingDeltaEvent.index = currentBlockIndex;
                    yield return ("content_block_delta", thinkingDeltaEvent);
                }

                // 处理结束
                if (!string.IsNullOrEmpty(choice.FinishReason) && !isFinished)
                {
                    isFinished = true;

                    // 发送content_block_stop事件（如果有活跃的内容块）
                    if (!string.IsNullOrEmpty(currentContentBlockType))
                    {
                        var contentBlockStopEvent = CreateContentBlockStopEvent();
                        contentBlockStopEvent.index = currentBlockIndex;
                        yield return ("content_block_stop", contentBlockStopEvent);
                    }

                    // 发送message_delta事件
                    var messageDeltaEvent = CreateMessageDeltaEvent(
                        GetStopReasonByLastContentType(choice.FinishReason, lastContentBlockType), accumulatedUsage);
                    yield return ("message_delta", messageDeltaEvent);

                    // 发送message_stop事件
                    var messageStopEvent = CreateMessageStopEvent();
                    yield return ("message_stop", messageStopEvent);
                }
            }

            // 更新使用情况统计
            if (openAIResponse.Usage != null)
            {
                accumulatedUsage.input_tokens = openAIResponse.Usage.PromptTokens ?? accumulatedUsage.input_tokens;
                accumulatedUsage.output_tokens =
                    (int?)(openAIResponse.Usage.CompletionTokens ?? accumulatedUsage.output_tokens);
                accumulatedUsage.cache_read_input_tokens = openAIResponse.Usage.PromptTokensDetails?.CachedTokens ??
                                                           accumulatedUsage.cache_read_input_tokens;
            }
        }

        // 确保流正确结束
        if (!isFinished)
        {
            if (!string.IsNullOrEmpty(currentContentBlockType))
            {
                var contentBlockStopEvent = CreateContentBlockStopEvent();
                contentBlockStopEvent.index = currentBlockIndex;
                yield return ("content_block_stop", contentBlockStopEvent);
            }

            var messageDeltaEvent =
                CreateMessageDeltaEvent(GetStopReasonByLastContentType("end_turn", lastContentBlockType),
                    accumulatedUsage);
            yield return ("message_delta", messageDeltaEvent);

            var messageStopEvent = CreateMessageStopEvent();
            yield return ("message_stop", messageStopEvent);
        }
    }

    /// <summary>
    /// 将AnthropicInput转换为ThorChatCompletionsRequest
    /// </summary>
    private ThorChatCompletionsRequest ConvertAnthropicToOpenAI(AnthropicInput anthropicInput)
    {
        var openAIRequest = new ThorChatCompletionsRequest
        {
            Model = anthropicInput.Model,
            MaxTokens = anthropicInput.MaxTokens,
            Stream = anthropicInput.Stream,
            Messages = new List<ThorChatMessage>()
        };

        if (!string.IsNullOrEmpty(anthropicInput.System))
        {
            openAIRequest.Messages.Add(ThorChatMessage.CreateSystemMessage(anthropicInput.System));
        }

        if (anthropicInput.Systems?.Count > 0)
        {
            foreach (var systemContent in anthropicInput.Systems)
            {
                openAIRequest.Messages.Add(ThorChatMessage.CreateSystemMessage(systemContent.Text ?? string.Empty));
            }
        }


        // 处理messages
        if (anthropicInput.Messages != null)
        {
            foreach (var message in anthropicInput.Messages)
            {
                var thorMessages = ConvertAnthropicMessageToThor(message);
                // 需要过滤 空消息
                if (thorMessages.Count == 0)
                {
                    continue;
                }

                openAIRequest.Messages.AddRange(thorMessages);
            }

            openAIRequest.Messages = openAIRequest.Messages
                .Where(m => !string.IsNullOrEmpty(m.Content) || m.Contents?.Count > 0 || m.ToolCalls?.Count > 0 ||
                            !string.IsNullOrEmpty(m.ToolCallId))
                .ToList();
        }

        // 处理tools
        if (anthropicInput.Tools is { Count: > 0 })
        {
            openAIRequest.Tools = anthropicInput.Tools.Select(ConvertAnthropicToolToThor).ToList();
        }

        // 处理tool_choice
        if (anthropicInput.ToolChoice != null)
        {
            openAIRequest.ToolChoice = ConvertAnthropicToolChoiceToThor(anthropicInput.ToolChoice);
        }

        return openAIRequest;
    }

    /// <summary>
    /// 转换Anthropic消息为Thor消息列表
    /// </summary>
    private List<ThorChatMessage> ConvertAnthropicMessageToThor(AnthropicMessageInput anthropicMessage)
    {
        var results = new List<ThorChatMessage>();

        // 处理简单的字符串内容
        if (anthropicMessage.Content != null)
        {
            var thorMessage = new ThorChatMessage
            {
                Role = anthropicMessage.Role,
                Content = anthropicMessage.Content
            };
            results.Add(thorMessage);
            return results;
        }

        // 处理多模态内容
        if (anthropicMessage.Contents is { Count: > 0 })
        {
            var currentContents = new List<ThorChatMessageContent>();
            var currentToolCalls = new List<ThorToolCall>();

            foreach (var content in anthropicMessage.Contents)
            {
                if (content.Type == "text")
                {
                    currentContents.Add(ThorChatMessageContent.CreateTextContent(content.Text ?? string.Empty));
                }
                else if (content.Type == "image")
                {
                    if (content.Source != null)
                    {
                        var imageUrl = content.Source.Type == "base64"
                            ? $"data:{content.Source.MediaType};base64,{content.Source.Data}"
                            : content.Source.Data;
                        currentContents.Add(ThorChatMessageContent.CreateImageUrlContent(imageUrl ?? string.Empty));
                    }
                }
                else if (content.Type == "tool_use")
                {
                    // 如果有普通内容，先创建内容消息
                    if (currentContents.Count > 0)
                    {
                        if (currentContents.Count == 1 && currentContents.Any(x => x.Type == "text"))
                        {
                            var contentMessage = new ThorChatMessage
                            {
                                Role = anthropicMessage.Role,
                                ContentCalculated = currentContents.FirstOrDefault()?.Text ?? string.Empty
                            };
                            results.Add(contentMessage);
                        }
                        else
                        {
                            var contentMessage = new ThorChatMessage
                            {
                                Role = anthropicMessage.Role,
                                Contents = currentContents
                            };
                            results.Add(contentMessage);
                        }

                        currentContents = new List<ThorChatMessageContent>();
                    }

                    // 收集工具调用
                    var toolCall = new ThorToolCall
                    {
                        Id = content.Id,
                        Type = "function",
                        Function = new ThorChatMessageFunction
                        {
                            Name = content.Name,
                            Arguments = JsonSerializer.Serialize(content.Input)
                        }
                    };
                    currentToolCalls.Add(toolCall);
                }
                else if (content.Type == "tool_result")
                {
                    // 如果有普通内容，先创建内容消息
                    if (currentContents.Count > 0)
                    {
                        var contentMessage = new ThorChatMessage
                        {
                            Role = anthropicMessage.Role,
                            Contents = currentContents
                        };
                        results.Add(contentMessage);
                        currentContents = new List<ThorChatMessageContent>();
                    }

                    // 如果有工具调用，先创建工具调用消息
                    if (currentToolCalls.Count > 0)
                    {
                        var toolCallMessage = new ThorChatMessage
                        {
                            Role = anthropicMessage.Role,
                            ToolCalls = currentToolCalls
                        };
                        results.Add(toolCallMessage);
                        currentToolCalls = new List<ThorToolCall>();
                    }

                    // 创建工具结果消息
                    var toolMessage = new ThorChatMessage
                    {
                        Role = "tool",
                        ToolCallId = content.ToolUseId,
                        Content = content.Content?.ToString() ?? string.Empty
                    };
                    results.Add(toolMessage);
                }
            }

            // 处理剩余的内容
            if (currentContents.Count > 0)
            {
                var contentMessage = new ThorChatMessage
                {
                    Role = anthropicMessage.Role,
                    Contents = currentContents
                };
                results.Add(contentMessage);
            }

            // 处理剩余的工具调用
            if (currentToolCalls.Count > 0)
            {
                var toolCallMessage = new ThorChatMessage
                {
                    Role = anthropicMessage.Role,
                    ToolCalls = currentToolCalls
                };
                results.Add(toolCallMessage);
            }
        }

        // 如果没有任何内容，返回一个空的消息
        if (results.Count == 0)
        {
            results.Add(new ThorChatMessage
            {
                Role = anthropicMessage.Role,
                Content = string.Empty
            });
        }

        // 如果只有一个text则使用content字段
        if (results is [{ Contents.Count: 1 }] &&
            results.FirstOrDefault()?.Contents?.FirstOrDefault()?.Type == "text" &&
            !string.IsNullOrEmpty(results.FirstOrDefault()?.Contents?.FirstOrDefault()?.Text))
        {
            return
            [
                new ThorChatMessage
                {
                    Role = results[0].Role,
                    Content = results.FirstOrDefault()?.Contents?.FirstOrDefault()?.Text ?? string.Empty
                }
            ];
        }

        return results;
    }

    /// <summary>
    /// 转换Anthropic工具为Thor工具
    /// </summary>
    private ThorToolDefinition ConvertAnthropicToolToThor(AnthropicMessageTool anthropicTool)
    {
        IDictionary<string, ThorToolFunctionPropertyDefinition> values =
            new Dictionary<string, ThorToolFunctionPropertyDefinition>();

        if (anthropicTool.InputSchema?.Properties != null)
        {
            foreach (var property in anthropicTool.InputSchema.Properties)
            {
                var propertyValueStr = property.Value?.ToString();
                if (propertyValueStr != null)
                {
                    try
                    {
                        var propertyDefinition =
                            JsonSerializer.Deserialize<ThorToolFunctionPropertyDefinition>(propertyValueStr);
                        if (propertyDefinition != null)
                        {
                            values.Add(property.Key, propertyDefinition);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("测试：" + propertyValueStr);
                        throw;
                    }
                }
            }
        }


        return new ThorToolDefinition
        {
            Type = "function",
            Function = new ThorToolFunctionDefinition
            {
                Name = anthropicTool.name,
                Description = anthropicTool.Description,
                Parameters = new ThorToolFunctionPropertyDefinition
                {
                    Type = anthropicTool.InputSchema?.Type ?? "object",
                    Properties = values,
                    Required = anthropicTool.InputSchema?.Required?.ToList() ?? new List<string>()
                }
            }
        };
    }

    /// <summary>
    /// 转换Anthropic工具选择为Thor工具选择
    /// </summary>
    private ThorToolChoice ConvertAnthropicToolChoiceToThor(AnthropicTooChoiceInput anthropicToolChoice)
    {
        return new ThorToolChoice
        {
            Type = anthropicToolChoice.Type ?? "auto",
            Function = anthropicToolChoice.Name != null
                ? new ThorToolChoiceFunctionTool { Name = anthropicToolChoice.Name }
                : null
        };
    }

    /// <summary>
    /// 将OpenAI响应转换为Claude响应格式
    /// </summary>
    private ClaudeChatCompletionDto ConvertOpenAIToClaude(ThorChatCompletionsResponse openAIResponse,
        AnthropicInput originalRequest)
    {
        var claudeResponse = new ClaudeChatCompletionDto
        {
            id = openAIResponse.Id,
            type = "message",
            role = "assistant",
            model = openAIResponse.Model ?? originalRequest.Model,
            stop_reason = GetClaudeStopReason(openAIResponse.Choices?.FirstOrDefault()?.FinishReason),
            stop_sequence = "",
            content = new ClaudeChatCompletionDtoContent[0]
        };

        if (openAIResponse.Choices != null && openAIResponse.Choices.Count > 0)
        {
            var choice = openAIResponse.Choices.First();
            var contents = new List<ClaudeChatCompletionDtoContent>();

            // 处理思维内容
            if (!string.IsNullOrEmpty(choice.Message.ReasoningContent))
            {
                contents.Add(new ClaudeChatCompletionDtoContent
                {
                    type = "thinking",
                    Thinking = choice.Message.ReasoningContent
                });
            }

            // 处理文本内容
            if (!string.IsNullOrEmpty(choice.Message.Content))
            {
                contents.Add(new ClaudeChatCompletionDtoContent
                {
                    type = "text",
                    text = choice.Message.Content
                });
            }

            // 处理工具调用
            if (choice.Message.ToolCalls != null && choice.Message.ToolCalls.Count > 0)
            {
                foreach (var toolCall in choice.Message.ToolCalls)
                {
                    contents.Add(new ClaudeChatCompletionDtoContent
                    {
                        type = "tool_use",
                        id = toolCall.Id,
                        name = toolCall.Function?.Name,
                        input = JsonSerializer.Deserialize<object>(toolCall.Function?.Arguments ?? "{}")
                    });
                }
            }

            claudeResponse.content = contents.ToArray();
        }

        // 处理使用情况统计
        if (openAIResponse.Usage != null)
        {
            claudeResponse.Usage = new ClaudeChatCompletionDtoUsage
            {
                input_tokens = openAIResponse.Usage.PromptTokens,
                output_tokens = (int?)openAIResponse.Usage.CompletionTokens,
                cache_read_input_tokens = openAIResponse.Usage.PromptTokensDetails?.CachedTokens
            };
        }

        return claudeResponse;
    }


    /// <summary>
    /// 将OpenAI的完成原因转换为Claude的停止原因
    /// </summary>
    private string GetClaudeStopReason(string? openAIFinishReason)
    {
        return openAIFinishReason switch
        {
            "stop" => "end_turn",
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            "content_filter" => "stop_sequence",
            _ => "end_turn"
        };
    }

    /// <summary>
    /// 根据最后的内容块类型和OpenAI的完成原因确定Claude的停止原因
    /// </summary>
    private string GetStopReasonByLastContentType(string? openAIFinishReason, string lastContentBlockType)
    {
        // 如果最后一个内容块是工具调用，优先返回tool_use
        if (lastContentBlockType == "tool_use")
        {
            return "tool_use";
        }

        // 否则使用标准的转换逻辑
        return GetClaudeStopReason(openAIFinishReason);
    }

    /// <summary>
    /// 创建message_start事件
    /// </summary>
    private ClaudeStreamDto CreateMessageStartEvent(string messageId, string model)
    {
        return new ClaudeStreamDto
        {
            type = "message_start",
            message = new ClaudeChatCompletionDto
            {
                id = messageId,
                type = "message",
                role = "assistant",
                model = model,
                content = new ClaudeChatCompletionDtoContent[0],
                Usage = new ClaudeChatCompletionDtoUsage
                {
                    input_tokens = 0,
                    output_tokens = 0,
                    cache_creation_input_tokens = 0,
                    cache_read_input_tokens = 0
                }
            }
        };
    }

    /// <summary>
    /// 创建content_block_start事件
    /// </summary>
    private ClaudeStreamDto CreateContentBlockStartEvent()
    {
        return new ClaudeStreamDto
        {
            type = "content_block_start",
            index = 0,
            content_block = new ClaudeChatCompletionDtoContent_block
            {
                type = "text",
                id = null,
                name = null
            }
        };
    }

    /// <summary>
    /// 创建thinking block start事件
    /// </summary>
    private ClaudeStreamDto CreateThinkingBlockStartEvent()
    {
        return new ClaudeStreamDto
        {
            type = "content_block_start",
            index = 0,
            content_block = new ClaudeChatCompletionDtoContent_block
            {
                type = "thinking",
                id = null,
                name = null
            }
        };
    }

    /// <summary>
    /// 创建content_block_delta事件
    /// </summary>
    private ClaudeStreamDto CreateContentBlockDeltaEvent(string text)
    {
        return new ClaudeStreamDto
        {
            type = "content_block_delta",
            index = 0,
            delta = new ClaudeChatCompletionDtoDelta
            {
                type = "text_delta",
                text = text
            }
        };
    }

    /// <summary>
    /// 创建thinking delta事件
    /// </summary>
    private ClaudeStreamDto CreateThinkingBlockDeltaEvent(string thinking)
    {
        return new ClaudeStreamDto
        {
            type = "content_block_delta",
            index = 0,
            delta = new ClaudeChatCompletionDtoDelta
            {
                type = "thinking",
                thinking = thinking
            }
        };
    }

    /// <summary>
    /// 创建content_block_stop事件
    /// </summary>
    private ClaudeStreamDto CreateContentBlockStopEvent()
    {
        return new ClaudeStreamDto
        {
            type = "content_block_stop",
            index = 0
        };
    }

    /// <summary>
    /// 创建message_delta事件
    /// </summary>
    private ClaudeStreamDto CreateMessageDeltaEvent(string finishReason, ClaudeChatCompletionDtoUsage usage)
    {
        return new ClaudeStreamDto
        {
            type = "message_delta",
            Usage = usage,
            delta = new ClaudeChatCompletionDtoDelta
            {
                stop_reason = finishReason
            }
        };
    }

    /// <summary>
    /// 创建message_stop事件
    /// </summary>
    private ClaudeStreamDto CreateMessageStopEvent()
    {
        return new ClaudeStreamDto
        {
            type = "message_stop"
        };
    }

    /// <summary>
    /// 创建tool block start事件
    /// </summary>
    private ClaudeStreamDto CreateToolBlockStartEvent(string? toolId, string? toolName)
    {
        return new ClaudeStreamDto
        {
            type = "content_block_start",
            index = 0,
            content_block = new ClaudeChatCompletionDtoContent_block
            {
                type = "tool_use",
                id = toolId,
                name = toolName
            }
        };
    }

    /// <summary>
    /// 创建tool delta事件
    /// </summary>
    private ClaudeStreamDto CreateToolBlockDeltaEvent(string partialJson)
    {
        return new ClaudeStreamDto
        {
            type = "content_block_delta",
            index = 0,
            delta = new ClaudeChatCompletionDtoDelta
            {
                type = "input_json_delta",
                partial_json = partialJson
            }
        };
    }
}