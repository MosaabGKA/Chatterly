﻿using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Chatterly_Backend.Data;
using Chatterly_Backend.Data.DTOs;
using Chatterly_Backend.Data.Models;
using Microsoft.AspNetCore.SignalR;
using Chatterly_Backend.Hubs;

namespace Chatterly_Backend.Repositories
{
    public class ChatsRepository(ApplicationDbContext dbContext, IHubContext<MessagingHub> hubContext, ILogger<ChatsRepository> logger) : IChatsRepository
    {
        public async Task<Chat> CreateChatAsync(string name, IEnumerable<string> userIds)
        {
            var chat = new Chat() { Name = name, LastEdited = DateTime.UtcNow };
            await dbContext.Chats.AddAsync(chat);
            await dbContext.SaveChangesAsync();

            foreach (var userId in userIds)
            {
                await AddUserToChatAsync(chat.Id, userId);
            }

            return chat;
        }

        public async Task<List<ChatDTO>> GetUserChatsAsync(string userId)
        {
            var chats = await dbContext.Chats
                .OrderByDescending(c => c.LastEdited)
                .Where(chat => chat.Users.Any(user => user.Id == userId))
                .Where(chat => chat.Messages.Count() > 0 || chat.Users.Count > 2)
                .Include(c => c.Users)
                .Select(c => new ChatDTO()
                {
                    Id = c.Id,
                    Name = c.Name,
                    LastEdited = c.LastEdited,
                    Users = c.Users.Select(u => u.ToDTO()).ToList(),
                    Messages = c.Messages.Where(m => m.Id == c.Messages.OrderByDescending(m => m.PublishDate).First().Id).Select(m => m.ToDTO()).ToList()
                })
                .ToListAsync();

            chats.AsParallel().Where(c => c.Users.Count <= 2).ForAll(c =>
            {
                c.Name = c.Users.Where(u => u.Id != userId).Select(u => $"{u.FirstName} {u.LastName}").FirstOrDefault() ?? $"{c.Users.First().FirstName} {c.Users.First().LastName}";
                c.PhotoUrl = c.Users.Where(u => u.Id != userId).Select(u => u.PhotoUrl).FirstOrDefault() ?? c.Users.First().PhotoUrl;
            });

            return chats;
        }

        public async Task<ChatDTO> GetChatAsync(string callerId, int chatId)
        {
            var chat = await dbContext.Chats
                .Where(c => c.Id == chatId)
                .Include(chat => chat.Users)
                .Include(chat => chat.Messages)
                .Select(c => c.ToDTO())
                .FirstOrDefaultAsync() ?? throw new ArgumentException("Chat not found.");

            if (chat.Users.Count <= 2)
            {
                chat.Name = chat.Users.Where(u => u.Id != callerId).Select(u => $"{u.FirstName} {u.LastName}").FirstOrDefault() ?? $"{chat.Users.First().FirstName} {chat.Users.First().LastName}";
                chat.PhotoUrl = chat.Users.Where(u => u.Id != callerId).Select(u => u.PhotoUrl).FirstOrDefault() ?? chat.Users.First().PhotoUrl;
            }

            return chat;
        }

        public async Task<ChatDTO> GetChatForOtherAsync(string callerId, int chatId)
        {
            var chat = await dbContext.Chats
                .Where(c => c.Id == chatId)
                .Include(chat => chat.Users)
                .Include(chat => chat.Messages)
                .Select(c => c.ToDTO())
                .FirstOrDefaultAsync() ?? throw new ArgumentException("Chat not found.");

            if (chat.Users.Count <= 2)
            {
                chat.Name = chat.Users.Where(u => u.Id == callerId).Select(u => $"{u.FirstName} {u.LastName}").FirstOrDefault() ?? $"{chat.Users.First().FirstName} {chat.Users.First().LastName}";
                chat.PhotoUrl = chat.Users.Where(u => u.Id == callerId).Select(u => u.PhotoUrl).FirstOrDefault() ?? chat.Users.First().PhotoUrl;
            }

            return chat;
        }

        public async Task AddUserToChatAsync(int chatId, string userId)
        {
            var chat = await dbContext.Chats.FindAsync(chatId);
            var user = await dbContext.Users.FindAsync(userId);

            if (chat == null || user == null)
            {
                throw new ArgumentException("Chat or user not found");
            }

            chat.Users.Add(user);
            await dbContext.SaveChangesAsync();
        }

        public async Task RemoveUserFromChatAsync(int chatId, string userId)
        {
            var chat = await dbContext.Chats.FindAsync(chatId);
            var user = await dbContext.Users.FindAsync(userId);

            if (chat == null || user == null)
            {
                throw new ArgumentException("Chat or user not found");
            }

            chat.Users.Remove(user);
            await dbContext.SaveChangesAsync();
        }

        public async Task DeleteChatAsync(int chatId)
        {
            var chat = await dbContext.Chats.FindAsync(chatId);

            if (chat == null)
            {
                throw new ArgumentException("Chat not found");
            }

            dbContext.Chats.Remove(chat);
            await dbContext.SaveChangesAsync();
        }

        public async Task<ChatDTO> GetChatByParticipantsAsync(string conversationInitiatorId, string targetedGuyId)
        {
            ChatDTO? chat;
            if (conversationInitiatorId == targetedGuyId)
            {
                chat = (await dbContext.Chats.
                    FirstOrDefaultAsync(c => c.Users.All(u => u.Id == conversationInitiatorId)))?.ToDTO();
            }
            else
            {
                chat = (await dbContext.Chats.
                    FirstOrDefaultAsync(c => c.Users.Count() == 2 &&
                    c.Users.Any(u => u.Id == conversationInitiatorId) &&
                    c.Users.Any(u => u.Id == targetedGuyId)))?.ToDTO();
            }

            if (chat == null)
            {
                var targetedGuy = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == targetedGuyId)
                    ?? throw new ArgumentException("Targeted guy not found. Make sure you're a good stalker.");

                var conversationInitiator = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == conversationInitiatorId)
                    ?? throw new ArgumentException("Conversation initiator not found. Make sure you're a good hacker.");

                var cht = new Chat() { Name = targetedGuy.UserName!, LastEdited = DateTime.UtcNow };

                cht.Users.Add(conversationInitiator);
                cht.Users.Add(targetedGuy);

                await dbContext.Chats.AddAsync(cht);
                await dbContext.SaveChangesAsync();

                chat = cht.ToDTO();
            }
            else
            {
                var targetedGuy = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == targetedGuyId)
                    ?? throw new ArgumentException("Targeted guy not found. Make sure you're a good stalker.");

                chat.Name = targetedGuy.UserName!;
            }

            await hubContext.Clients.User(conversationInitiatorId).SendAsync("ChatCreated", chat);
            await hubContext.Clients.User(targetedGuyId).SendAsync("ChatCreated", chat);

            return chat;
        }
    }
}
