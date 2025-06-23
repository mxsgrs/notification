using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using System.ComponentModel.DataAnnotations;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseInMemoryDatabase("NotificationDb"));

builder.Services.AddSignalR();
builder.Services.AddSingleton<UserConnectionManager>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("https://localhost:7059", "http://localhost:7059", "null", "file://")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Configure CORS
app.UseCors();

// Serve static files for HTML client
app.UseStaticFiles();

// Initialize database with sample users
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if (!context.Users.Any())
    {
        context.Users.AddRange(
            new User { Id = 1 },
            new User { Id = 2 },
            new User { Id = 3 }
        );
        context.SaveChanges();
    }
}

// API endpoint to receive HTTP POST requests
app.MapPost("/api/notifications", async (NotificationRequest request, AppDbContext context, IHubContext<NotificationHub> hubContext, UserConnectionManager connectionManager) =>
{
    // Store the notification request
    var notification = new Notification
    {
        UserId = request.UserId,
        Message = request.Message,
        CreatedAt = DateTime.UtcNow
    };

    context.Notifications.Add(notification);
    await context.SaveChangesAsync();

    // Check if user exists
    var userExists = await context.Users.AnyAsync(u => u.Id == request.UserId);
    if (!userExists)
    {
        return Results.BadRequest("User not found");
    }

    // Send notification via SignalR to specific user connections (real-time delivery)
    var connectionIds = connectionManager.GetUserConnections(request.UserId);
    if (connectionIds.Any())
    {
        await hubContext.Clients.Clients(connectionIds)
            .SendAsync("ReceiveNotification", new
            {
                Id = notification.Id,
                Message = notification.Message,
                CreatedAt = notification.CreatedAt
            });
    }

    return Results.Created($"/api/notifications/{notification.Id}", notification);
});

// API endpoint to get notifications for a user
app.MapGet("/api/notifications/{userId}", async (int userId, AppDbContext context) =>
{
    var notifications = await context.Notifications
        .Where(n => n.UserId == userId)
        .OrderByDescending(n => n.CreatedAt)
        .Select(n => new
        {
            Id = n.Id,
            Message = n.Message,
            CreatedAt = n.CreatedAt,
            IsSent = context.UserNotifications
                .Any(un => un.NotificationId == n.Id && un.UserId == userId && un.IsSent)
        })
        .ToListAsync();

    return Results.Ok(notifications);
});

// API endpoint to get all users
app.MapGet("/api/users", async (AppDbContext context) =>
{
    var users = await context.Users.ToListAsync();
    return Results.Ok(users);
});

// Map SignalR hub
app.MapHub<NotificationHub>("/notificationHub");

// Serve the HTML client
app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();

// Connection Manager
public class UserConnectionManager
{
    private readonly ConcurrentDictionary<int, HashSet<string>> _userConnections = new();

    public void AddUserConnection(int userId, string connectionId)
    {
        _userConnections.AddOrUpdate(userId,
            new HashSet<string> { connectionId },
            (key, existing) =>
            {
                existing.Add(connectionId);
                return existing;
            });
    }

    public void RemoveUserConnection(int userId, string connectionId)
    {
        if (_userConnections.TryGetValue(userId, out var connections))
        {
            connections.Remove(connectionId);
            if (!connections.Any())
            {
                _userConnections.TryRemove(userId, out _);
            }
        }
    }

    public List<string> GetUserConnections(int userId)
    {
        return _userConnections.TryGetValue(userId, out var connections)
            ? connections.ToList()
            : new List<string>();
    }
}

// Entity Models
public class User
{
    public int Id { get; set; }
}

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class UserNotification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int NotificationId { get; set; }
    public bool IsSent { get; set; }
    public DateTime? SentAt { get; set; }
}

// Request Models
public class NotificationRequest
{
    [Required]
    public int UserId { get; set; }

    [Required]
    public string Message { get; set; } = string.Empty;
}

// DbContext
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<Notification> Notifications { get; set; }
    public DbSet<UserNotification> UserNotifications { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserNotification>()
            .HasIndex(un => new { un.UserId, un.NotificationId })
            .IsUnique();
    }
}

// SignalR Hub
public class NotificationHub : Hub
{
    private readonly AppDbContext _context;
    private readonly UserConnectionManager _connectionManager;

    public NotificationHub(AppDbContext context, UserConnectionManager connectionManager)
    {
        _context = context;
        _connectionManager = connectionManager;
    }

    public async Task JoinUserGroup(string userId)
    {
        var userIdInt = int.Parse(userId);

        // Add connection to user connection manager
        _connectionManager.AddUserConnection(userIdInt, Context.ConnectionId);

        // Get notifications that haven't been sent to this user
        var unsentNotifications = await _context.Notifications
            .Where(n => n.UserId == userIdInt &&
                       !_context.UserNotifications.Any(un => un.NotificationId == n.Id && un.UserId == userIdInt && un.IsSent))
            .OrderByDescending(n => n.CreatedAt)
            .Take(50) // Limit to last 50 notifications
            .Select(n => new
            {
                Id = n.Id,
                Message = n.Message,
                CreatedAt = n.CreatedAt
            })
            .ToListAsync();

        // Send unsent notifications to the user
        await Clients.Caller.SendAsync("LoadExistingNotifications", unsentNotifications);

        // Mark these notifications as sent
        foreach (var notification in unsentNotifications)
        {
            var userNotification = new UserNotification
            {
                UserId = userIdInt,
                NotificationId = notification.Id,
                IsSent = true,
                SentAt = DateTime.UtcNow
            };

            _context.UserNotifications.Add(userNotification);
        }

        if (unsentNotifications.Any())
        {
            await _context.SaveChangesAsync();
        }
    }

    public async Task LeaveUserGroup(string userId)
    {
        var userIdInt = int.Parse(userId);
        _connectionManager.RemoveUserConnection(userIdInt, Context.ConnectionId);
        await Task.CompletedTask;
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Remove connection from all users
        foreach (var userId in Enumerable.Range(1, 3)) // Assuming users 1-3
        {
            _connectionManager.RemoveUserConnection(userId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}