using FluentValidation;
using FluentValidation.AspNetCore;
using Ganss.Xss;
using HtmlBuilders;
using LiteDB;
using Markdig;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Scriban;
using System.Globalization;
using System.Text.RegularExpressions;
using static HtmlBuilders.HtmlTags;

const string DisplayDateFormat = "MMMM dd, yyyy";
const string HomePageName = "home-page";
const string HtmlMime = "text/html";

var builder = WebApplication.CreateBuilder();
builder.Services
  .AddSingleton<Wiki>()
  .AddSingleton<Render>()
  .AddAntiforgery()
  .AddMemoryCache();

builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();

// Load home page
app.MapGet("/", (Wiki wiki, Render render) =>
{
    Page? page = wiki.GetPage(HomePageName);

    if (page is not object)
        return Results.Redirect($"/{HomePageName}");

    var notifications = new List<(string message, string type)>
    {
        ("Welcome to your wiki!", "success"),
        ("Remember to save your changes!", "warning")
    };

    return Results.Text(render.BuildPage(HomePageName, atBody: () =>
        new[]
        {
          RenderPageContent(page),
          RenderPageAttachments(page),
          A.Href($"/edit?pageName={HomePageName}").Class("uk-button uk-button-default uk-button-small").Append("Edit").ToHtmlString()
        },
        atSidePanel: () => AllPages(wiki),
        notifications: notifications
    ).ToString(), HtmlMime);
});

app.MapGet("/new-page", (string? pageName, ILogger<Program> logger, Wiki wiki) =>
{
    if (string.IsNullOrWhiteSpace(pageName))
    {
        return Results.Redirect($"/?notification=Page%20name%20cannot%20be%20empty.&type=error");
    }

    string ToKebabCase(string str)
    {
        Regex pattern = new Regex(@"[A-Z]{2,}(?=[A-Z][a-z]+[0-9]*|\b)|[A-Z]?[a-z]+[0-9]*|[A-Z]|[0-9]+");
        return string.Join("-", pattern.Matches(str)).ToLower();
    }

    var sanitizedPageName = ToKebabCase(pageName);

    // Save new page if it doesn't exist
    var existingPage = wiki.GetPage(sanitizedPageName);
    if (existingPage == null)
    {
        var (isOk, _, ex) = wiki.SavePage(new PageInput(null, sanitizedPageName, "", null));
        if (!isOk)
        {
            return Results.Redirect($"/?notification=Failed%20to%20create%20page.&type=error");
        }
    }

    // Redirect with success notification
    return Results.Redirect($"/{sanitizedPageName}?notification=Page%20created%20successfully.&type=success");
});

// Edit a wiki page
app.MapGet("/edit", (string pageName, HttpContext context, Wiki wiki, Render render, IAntiforgery antiForgery) =>
{
    Page? page = wiki.GetPage(pageName);

    if (page == null)
    {
        // Notify the user and provide an empty form
        var notifications = new List<(string message, string type)>
        {
            ("Creating a new page. Please fill out the form and submit.", "info")
        };

        return Results.Text(render.BuildEditorPage(
            pageName,
            atBody: () => new[] {
                BuildForm(
                    new PageInput(null, pageName, string.Empty, null),
                    path: $"{pageName}",
                    antiForgery: antiForgery.GetAndStoreTokens(context)
                )
            },
            atSidePanel: () => AllPages(wiki),
            notifications: notifications
        ).ToString(), HtmlMime);
    }

    // Existing page editing logic
    var editNotifications = new List<(string message, string type)>
    {
        ("You are editing this page. Ensure you save your changes.", "warning"),
    };

    return Results.Text(render.BuildEditorPage(
        pageName,
        atBody: () => new[] {
            BuildForm(
                new PageInput(page.Id, pageName, page.Content, null),
                path: $"{pageName}",
                antiForgery: antiForgery.GetAndStoreTokens(context)
            ),
            RenderPageAttachmentsForEdit(page, antiForgery.GetAndStoreTokens(context))
        },
        atSidePanel: () =>
        {
            var list = new List<string>();
            if (!pageName.Equals(HomePageName, StringComparison.Ordinal))
                list.Add(RenderDeletePageButton(page, antiForgery.GetAndStoreTokens(context)));

            list.Add(Br.ToHtmlString());
            list.AddRange(AllPagesForEditing(wiki));
            return list;
        },
        notifications: editNotifications
    ).ToString(), HtmlMime);
});

// Deal with attachment download
app.MapGet("/attachment", (string fileId, Wiki wiki) =>
{
    var file = wiki.GetFile(fileId);
    if (file is not object)
      return Results.NotFound();

    app!.Logger.LogInformation("Attachment " + file.Value.meta.Id + " - " + file.Value.meta.Filename);

    return Results.File(file.Value.file, file.Value.meta.MimeType);
});

// Load a wiki page
app.MapGet("/{pageName}", (string pageName, HttpContext context, Wiki wiki, Render render) =>
{
    Page? page = wiki.GetPage(pageName);

    if (page is object)
    {
        return Results.Text(render.BuildPage(
            pageName,
            atBody: () => new[]
            {
                RenderPageContent(page),
                RenderPageAttachments(page),
                Div.Class("last-modified").Append($"Last modified: {page.LastModifiedUtc.ToString(DisplayDateFormat)}").ToHtmlString(),
                A.Href($"/edit?pageName={pageName}").Class("uk-button uk-button-default uk-button-small").Append("Edit").ToHtmlString()
            },
            atSidePanel: () => AllPages(wiki)
        ).ToString(), HtmlMime);
    }
    else
    {
        return Results.NotFound();
    }
});

// Delete a page
app.MapPost("/delete-page", async (HttpContext context, IAntiforgery antiForgery, Wiki wiki) =>
{
    await antiForgery.ValidateRequestAsync(context);
    var id = context.Request.Form["Id"];

    if (StringValues.IsNullOrEmpty(id))
    {
        var query = "?notification=Missing%20page%20details.&type=error";
        return Results.Redirect($"/{HomePageName}{query}");
    }

    var (isOk, exception) = wiki.DeletePage(Convert.ToInt32(id), HomePageName);

    if (!isOk)
    {
        var query = $"?notification=Failed%20to%20delete%20page.&type=error";
        return Results.Redirect($"/{HomePageName}{query}");
    }

    // Redirect with success notification
    var successQuery = "?notification=Page%20deleted%20successfully.&type=success";
    return Results.Redirect($"/{HomePageName}{successQuery}");
});

app.MapPost("/delete-attachment", async (HttpContext context, IAntiforgery antiForgery, Wiki wiki) =>
{
    await antiForgery.ValidateRequestAsync(context);

    var id = context.Request.Form["Id"];
    var pageId = context.Request.Form["PageId"];

    if (StringValues.IsNullOrEmpty(id) || StringValues.IsNullOrEmpty(pageId))
    {
        // Redirect with proper query string
        return Results.Redirect($"/edit?pageName={HomePageName}&notification=Missing%20attachment%20details.&type=error");
    }

    var (isOk, page, exception) = wiki.DeleteAttachment(Convert.ToInt32(pageId), id.ToString());

    if (!isOk)
    {
        var errorMessage = exception != null
            ? Uri.EscapeDataString($"Failed to delete attachment: {exception.Message}")
            : Uri.EscapeDataString("Failed to delete attachment.");
        return Results.Redirect($"/edit?pageName={page?.Name ?? HomePageName}&notification={errorMessage}&type=error");
    }

    return Results.Redirect($"/edit?pageName={page!.Name}&notification=Attachment%20deleted%20successfully.&type=success");
});

// Add or update a wiki page
app.MapPost("/{pageName}", async (HttpContext context, Wiki wiki, Render render, IAntiforgery antiForgery) =>
{
    var pageName = context.Request.RouteValues["pageName"] as string ?? "";
    await antiForgery.ValidateRequestAsync(context);

    PageInput input = PageInput.From(context.Request.Form);

    var modelState = new ModelStateDictionary();
    var validator = new PageInputValidator(pageName, HomePageName);
    validator.Validate(input).AddToModelState(modelState, null);

    if (!modelState.IsValid)
    {
        return Results.Text(render.BuildPage(pageName,
          atBody: () =>
            new[]
            {
              BuildForm(input, path: $"{pageName}", antiForgery: antiForgery.GetAndStoreTokens(context), modelState)
            },
          atSidePanel: () => AllPages(wiki),
          notifications: new List<(string message, string type)>
          {
              ("Failed to save the page. Please fix the errors.", "error")
          }
        ).ToString(), HtmlMime);
    }

    var (isOk, p, ex) = wiki.SavePage(input);
    if (!isOk)
    {
        return Results.Problem("Problem in saving page");
    }

    // Add a success notification when the page is saved
    var successQuery = $"?notification=Page%20saved%20successfully.&type=success";
    return Results.Redirect($"/{p!.Name}{successQuery}");
});

await app.RunAsync();

// End of the web part

static string[] AllPages(Wiki wiki) => new[]
{
  @"<span class=""uk-label"">Pages</span>",
  @"<ul class=""uk-list"">",
  string.Join("",
    wiki.ListAllPages().OrderBy(x => x.Name)
      .Select(x => Li.Append(A.Href(x.Name).Append(x.Name)).ToHtmlString()
    )
  ),
  "</ul>"
};

static string[] AllPagesForEditing(Wiki wiki)
{
    static string KebabToNormalCase(string txt) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));

    return new[]
    {
      @"<span class=""uk-label"">Pages</span>",
      @"<ul class=""uk-list"">",
      string.Join("",
        wiki.ListAllPages().OrderBy(x => x.Name)
          .Select(x => Li.Append(Div.Class("uk-inline")
              .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
              .Append(Input.Text.Value($"[{KebabToNormalCase(x.Name)}](/{x.Name})").Class("uk-input uk-form-small").Style("cursor", "pointer").Attribute("onclick", "copyMarkdownLink(this);"))
          ).ToHtmlString()
        )
      ),
      "</ul>"
    };
}

static string RenderMarkdown(string? str)
{
    if (string.IsNullOrWhiteSpace(str))
        return "<p></p>"; // Return an empty paragraph for null or empty content

    var sanitizer = new HtmlSanitizer();
    return sanitizer.Sanitize(Markdown.ToHtml(str, new MarkdownPipelineBuilder().UseSoftlineBreakAsHardlineBreak().UseAdvancedExtensions().Build()));
}


static string RenderPageContent(Page page) => RenderMarkdown(page.Content ?? "");

static string RenderDeletePageButton(Page page, AntiforgeryTokenSet antiForgery)
{
    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
    HtmlTag id = Input.Hidden.Name("Id").Value(page.Id.ToString());
    var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-danger").Append("Delete Page"));

    var form = Form
               .Attribute("method", "post")
               .Attribute("action", $"/delete-page")
               .Attribute("onsubmit", $"return confirm('Please confirm to delete this page');")
                 .Append(antiForgeryField)
                 .Append(id)
                 .Append(submit);

    return form.ToHtmlString();
}

static string RenderPageAttachmentsForEdit(Page page, AntiforgeryTokenSet antiForgery)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var list = Ul.Class("uk-list");

    foreach (var attachment in page.Attachments)
    {
        var attachmentPreview = attachment.MimeType.StartsWith("image/")
    ? Img.Src($"/attachment?fileId={attachment.FileId}")
        .Class("uk-thumbnail") // Add a class for styling
        .Attribute("style", "max-width: 100%; height: auto;") // Correct way to add styles
    : Span.Class("uk-text").Append(attachment.FileName); // Non-image attachments


        list = list.Append(Li
    .Append(attachmentPreview)
    .Append(RenderDeleteAttachmentForm(page.Id, attachment.FileId, antiForgery))); // Append delete form

    }

    return label.ToHtmlString() + list.ToHtmlString();
}
static HtmlTag RenderDeleteAttachmentForm(int pageId, string attachmentId, AntiforgeryTokenSet antiForgery)
{
    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
    var idField = Input.Hidden.Name("Id").Value(attachmentId);
    var pageIdField = Input.Hidden.Name("PageId").Value(pageId.ToString());

    var submitButton = Button
        .Class("uk-button uk-button-danger uk-button-small")
        .Append(Span.Attribute("uk-icon", "icon: close; ratio: .75;"));

    return Form
        .Attribute("style", "display: inline;") // Corrected style attribute
        .Attribute("method", "post")
        .Attribute("action", "/delete-attachment")
        .Attribute("onsubmit", "return confirm('Are you sure you want to delete this attachment?');")
        .Append(antiForgeryField)
        .Append(idField)
        .Append(pageIdField)
        .Append(submitButton);
}


static string RenderPageAttachments(Page page)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var list = Ul.Class("uk-list uk-list-disc");
    static HtmlTag Br() => new HtmlTag("br");

    foreach (var attachment in page.Attachments)
    {
        if (attachment.MimeType.StartsWith("image/")) // Check if it's an image
        {
            list = list.Append(Li
    .Append(Img.Src($"/attachment?fileId={attachment.FileId}") // Image tag
        .Class("uk-thumbnail") // Add a class for styling
        .Attribute("style", "max-width: 100%; height: auto;")) // Correct way to add inline styles
    .Append(Br()) // Add a line break
    .Append(A.Href($"/attachment?fileId={attachment.FileId}").Append("Download"))); // Add a download link

        }
        else
        {
            list = list.Append(Li
                .Append(A.Href($"/attachment?fileId={attachment.FileId}").Append(attachment.FileName)));
        }
    }

    return label.ToHtmlString() + list.ToHtmlString();
}

// Build the wiki input form 
static string BuildForm(PageInput input, string path, AntiforgeryTokenSet antiForgery, ModelStateDictionary? modelState = null)
{
    bool IsFieldOK(string key) => modelState!.ContainsKey(key) && modelState[key]!.ValidationState == ModelValidationState.Invalid;

    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);

    var nameField = Div
      .Append(Label.Class("uk-form-label").Append(nameof(input.Name)))
      .Append(Div.Class("uk-form-controls")
        .Append(Input.Text.Class("uk-input").Name("Name").Value(input.Name))
      );

    var contentField = Div
      .Append(Label.Class("uk-form-label").Append(nameof(input.Content)))
      .Append(Div.Class("uk-form-controls")
        .Append(Textarea.Name("Content").Class("uk-textarea").Append(input.Content))
      );

    var attachmentField = Div
      .Append(Label.Class("uk-form-label").Append(nameof(input.Attachment)))
      .Append(Div.Attribute("uk-form-custom", "target: true")
        .Append(Input.File.Name("Attachment"))
        .Append(Input.Text.Class("uk-input uk-form-width-large").Attribute("placeholder", "Click to select file").ToggleAttribute("disabled", true))
      );

    if (modelState is object && !modelState.IsValid)
    {
        if (IsFieldOK("Name"))
        {
            foreach (var er in modelState["Name"]!.Errors)
            {
                nameField = nameField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
            }
        }

        if (IsFieldOK("Content"))
        {
            foreach (var er in modelState["Content"]!.Errors)
            {
                contentField = contentField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
            }
        }
    }

    var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-primary").Append("Submit"));

    var form = Form
               .Class("uk-form-stacked")
               .Attribute("method", "post")
               .Attribute("enctype", "multipart/form-data")
               .Attribute("action", $"/{path}")
                 .Append(antiForgeryField)
                 .Append(nameField)
                 .Append(contentField)
                 .Append(attachmentField);

    if (input.Id is object)
    {
        HtmlTag id = Input.Hidden.Name("Id").Value(input.Id.ToString()!);
        form = form.Append(id);
    }

    form = form.Append(submit);

    return form.ToHtmlString();
}

class Render
{
    static string KebabToNormalCase(string txt) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));

    static string[] MarkdownEditorHead() => new[]
    {
        @"<link rel=""stylesheet"" href=""https://unpkg.com/easymde/dist/easymde.min.css"">",
        @"<script src=""https://unpkg.com/easymde/dist/easymde.min.js""></script>"
    };

    static string[] MarkdownEditorFoot() => new[]
    {
        @"<script>
            var easyMDE = new EasyMDE({
                insertTexts: {
                    link: [""["", ""]()""]
                }
            });

            function copyMarkdownLink(element) {
                element.select();
                document.execCommand(""copy"");
            }
        </script>"
    };

    (Template head, Template body, Template layout) _templates = (
        head: Scriban.Template.Parse(
        """
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{ title }}</title>
        <link href="https://fonts.googleapis.com/css2?family=Roboto:wght@400;500;700&display=swap" rel="stylesheet">
        <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/css/uikit.min.css" />
        {{ header }}
        <style>
            /* Base Styles */
            html, body {
                margin: 0;
                padding: 0;
                width: 100%;
                height: 100%;
                font-family: 'Roboto', sans-serif;
                line-height: 1.6;
                transition: background-color 0.3s ease, color 0.3s ease;
            }

            /* Light Mode */
            body {
                background-color: #ffffff;
                color: #000000;
            }

            a {
                color: #1e88e5;
                text-decoration: none;
            }

            a:hover {
                text-decoration: underline;
            }

            .uk-thumbnail {
                border: 1px solid #ddd;
                border-radius: 4px;
                padding: 5px;
                max-width: 100%;
                height: auto;
            }

            /* Dark Mode */
            .dark-mode {
                background-color: #1f1f1f;
                color: #e0e0e0;
            }

            .dark-mode a {
                color: #bb86fc;
            }

            .dark-mode a:hover {
                color: #03dac6;
            }

            .dark-mode .uk-thumbnail {
                border-color: #333;
            }

            .dark-mode html, .dark-mode body {
                background-color: #1f1f1f;
                color: #e0e0e0;
            }

            .dark-mode .uk-navbar-container {
                background-color: #1f1f1f;
                border-bottom: 1px solid #333;
            }

            .dark-mode .uk-button-primary {
                background-color: #bb86fc;
                color: #1f1f1f;
                border: none;
                transition: transform 0.2s ease, background-color 0.2s ease;
            }

            .dark-mode .uk-button-primary:hover {
                background-color: #3700b3;
                transform: scale(1.05);
            }

            /* Notification Container */
            .notification-container {
                position: fixed;
                bottom: 10px;
                right: 10px;
                z-index: 1000;
                width: 300px;
                max-width: 90%;
            }

            /* Notification Styles */
            .notification {
                padding: 15px 20px;
                margin-bottom: 10px;
                border-radius: 5px;
                color: #ffffff;
                font-size: 16px;
                box-shadow: 0px 4px 6px rgba(0, 0, 0, 0.1);
                animation: fadeIn 0.5s ease;
            }

            .notification-success {
                background-color: #4CAF50;
            }

            .notification-error {
                background-color: #F44336;
            }

            .notification-warning {
                background-color: #FFC107;
                color: #000000;
            }

            /* Fade Out Animation */
            @keyframes fadeIn {
                from {
                    opacity: 0;
                    transform: translateY(-10px);
                }
                to {
                    opacity: 1;
                    transform: translateY(0);
                }
            }

            @keyframes fadeOut {
                from {
                    opacity: 1;
                }
                to {
                    opacity: 0;
                }
            }

            .fade-out {
                animation: fadeOut 0.5s ease forwards;
            }
        </style>
        """
        ),
        body: Scriban.Template.Parse("""
            <nav class="uk-navbar-container">
                <div class="uk-container">
                    <div class="uk-navbar">
                        <div class="uk-navbar-left">
                            <ul class="uk-navbar-nav">
                                <li class="uk-active"><a href="/"><span uk-icon="home"></span></a></li>
                            </ul>
                        </div>
                        <div class="uk-navbar-center">
                            <div class="uk-navbar-item">
                                <form action="/new-page">
                                    <input class="uk-input uk-form-width-large" type="text" name="pageName" placeholder="Type desired page title here"></input>
                                    <input type="submit" class="uk-button uk-button-default" value="Add New Page">
                                </form>
                            </div>
                        </div>
                        <div class="uk-navbar-right">
                            <button class="uk-button uk-button-primary" onclick="toggleDarkMode()">Toggle Dark Mode</button>
                        </div>
                    </div>
                </div>
            </nav>

            <!-- Notification Container -->
            <div class="notification-container" id="notification-container"></div>

            {{ if at_side_panel != "" }}
                <div class="uk-container">
                    <div uk-grid>
                        <div class="uk-width-4-5">
                            <h1>{{ page_name }}</h1>
                            {{ content }}
                        </div>
                        <div class="uk-width-1-5">
                            {{ at_side_panel }}
                        </div>
                    </div>
                </div>
            {{ else }}
                <div class="uk-container">
                    <h1>{{ page_name }}</h1>
                    {{ content }}
                </div>
            {{ end }}
            <script>
                // Apply dark mode if the preference is stored
                if (localStorage.getItem('dark-mode') === 'true') {
                    document.body.classList.add('dark-mode');
                    document.documentElement.classList.add('dark-mode'); // Apply to <html> as well
                }

                function toggleDarkMode() {
                    const body = document.body;
                    const html = document.documentElement; // Target <html>
                    const isDarkMode = body.classList.toggle('dark-mode');
                    html.classList.toggle('dark-mode'); // Add/remove class to <html>

                    // Save preference to localStorage
                    localStorage.setItem('dark-mode', isDarkMode);

                    // Notify user
                    showNotification(
                        isDarkMode ? 'Dark mode enabled!' : 'Light mode enabled!',
                        'success'
                    );
                }

                // Function to display a notification
                function showNotification(message, type = "success", duration = 5000) {
                    const container = document.getElementById("notification-container");
                    const notification = document.createElement("div");

                    notification.className = `notification notification-${type}`;
                    notification.textContent = message;

                    container.appendChild(notification);

                    setTimeout(() => {
                        notification.classList.add("fade-out");
                        notification.addEventListener("animationend", () => notification.remove());
                    }, duration);
                }
            </script>
            <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit.min.js"></script>
            <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit-icons.min.js"></script>
            {{ at_foot }}
        """),
        layout: Scriban.Template.Parse("""
            <!DOCTYPE html>
            <head>
                {{ head }}
            </head>
            <body>
                {{ body }}
            </body>
            </html>
        """)
    );

    public HtmlString BuildEditorPage(string title, Func<IEnumerable<string>> atBody, Func<IEnumerable<string>>? atSidePanel = null, List<(string message, string type)>? notifications = null) =>
        BuildPage(
            title,
            atHead: () => MarkdownEditorHead(),
            atBody: atBody,
            atSidePanel: atSidePanel,
            atFoot: () => MarkdownEditorFoot()
        );

    public HtmlString BuildPage(
        string title,
        Func<IEnumerable<string>>? atHead = null,
        Func<IEnumerable<string>>? atBody = null,
        Func<IEnumerable<string>>? atSidePanel = null,
        Func<IEnumerable<string>>? atFoot = null,
        List<(string message, string type)>? notifications = null)
    {
        var head = _templates.head.Render(new
        {
            title,
            header = string.Join("\r", atHead?.Invoke() ?? new[] { "" })
        });

        var body = _templates.body.Render(new
        {
            PageName = KebabToNormalCase(title),
            Content = string.Join("\r", atBody?.Invoke() ?? new[] { "" }),
            AtSidePanel = string.Join("\r", atSidePanel?.Invoke() ?? new[] { "" }),
            AtFoot = string.Join("\r", atFoot?.Invoke() ?? new[] { "" })
        });

        var notificationScript = string.Empty;
        if (notifications != null && notifications.Count > 0)
        {
            var notificationArray = notifications.Select(n => $"{{ message: \"{n.message}\", type: \"{n.type}\" }}").ToArray();
            notificationScript = $@"
                <script>
                    const notifications = [{string.Join(", ", notificationArray)}];
                    notifications.forEach(n => showNotification(n.message, n.type));
                </script>
            ";
        }

        var fullContent = _templates.layout.Render(new { head, body }) + notificationScript;

        return new HtmlString(fullContent);
    }
}

class Wiki
{
    DateTime Timestamp() => DateTime.UtcNow;

    const string PageCollectionName = "Pages";
    const string AllPagesKey = "AllPages";
    const double CacheAllPagesForMinutes = 30;

    readonly IWebHostEnvironment _env;
    readonly IMemoryCache _cache;
    readonly ILogger _logger;

    public Wiki(IWebHostEnvironment env, IMemoryCache cache, ILogger<Wiki> logger)
    {
        _env = env;
        _cache = cache;
        _logger = logger;
    }

    // Get the location of the LiteDB file.
    string GetDbPath() => Path.Combine(_env.ContentRootPath, "wiki.db");

    // List all the available wiki pages. It is cached for 30 minutes.
    public List<Page> ListAllPages()
    {
        var pages = _cache.Get(AllPagesKey) as List<Page>;

        if (pages is object)
            return pages;

        using var db = new LiteDatabase(GetDbPath());
        var coll = db.GetCollection<Page>(PageCollectionName);
        var items = coll.Query().ToList();

        _cache.Set(AllPagesKey, items, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(CacheAllPagesForMinutes)));
        return items;
    }

    // Get a wiki page based on its path
    public Page? GetPage(string path)
    {
        using var db = new LiteDatabase(GetDbPath());
        var coll = db.GetCollection<Page>(PageCollectionName);
        coll.EnsureIndex(x => x.Name);

        return coll.Query()
                .Where(x => x.Name.Equals(path, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
    }

    // Save or update a wiki page. Cache(AllPagesKey) will be destroyed.
public (bool isOk, Page? page, Exception? ex) SavePage(PageInput input)
{
    try
    {
        using var db = new LiteDatabase(GetDbPath());
        var coll = db.GetCollection<Page>(PageCollectionName);
        coll.EnsureIndex(x => x.Name);

        Page? existingPage = input.Id.HasValue ? coll.FindOne(x => x.Id == input.Id) : null;

        var sanitizer = new HtmlSanitizer();
        var properName = input.Name.ToString().Trim().Replace(' ', '-').ToLower();

        if (existingPage == null)
        {
            var newPage = new Page
            {
                Name = sanitizer.Sanitize(properName),
                Content = input.Content,
                LastModifiedUtc = Timestamp()
            };

            coll.Insert(newPage);

            _cache.Remove(AllPagesKey); // Refresh cache
            return (true, newPage, null);
        }

        // Handle updates for existing pages
        var updatedPage = existingPage with
        {
            Name = sanitizer.Sanitize(properName),
            Content = input.Content,
            LastModifiedUtc = Timestamp()
        };

        coll.Update(updatedPage);

        _cache.Remove(AllPagesKey);
        return (true, updatedPage, null);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, $"Error saving page: {input.Name}");
        return (false, null, ex);
    }
}

    public (bool isOk, Page? p, Exception? ex) DeleteAttachment(int pageId, string id)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);
            var page = coll.FindById(pageId);
            if (page is not object)
            {
                _logger.LogWarning($"Delete attachment operation fails because page id {id} cannot be found in the database");
                return (false, null, null);
            }

            if (!db.FileStorage.Delete(id))
            {
                _logger.LogWarning($"We cannot delete this file attachment id {id} and it's a mystery why");
                return (false, page, null);
            }

            page.Attachments.RemoveAll(x => x.FileId.Equals(id, StringComparison.OrdinalIgnoreCase));

            var updateResult = coll.Update(page);

            if (!updateResult)
            {
                _logger.LogWarning($"Delete attachment works but updating the page (id {pageId}) attachment list fails");
                return (false, page, null);
            }

            return (true, page, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex);
        }
    }

    public (bool isOk, Exception? ex) DeletePage(int id, string homePageName)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);

            var page = coll.FindById(id);

            if (page is not object)
            {
                _logger.LogWarning($"Delete operation fails because page id {id} cannot be found in the database");
                return (false, null);
            }

            if (page.Name.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"Page id {id}  is a home page and elete operation on home page is not allowed");
                return (false, null);
            }

            //Delete all the attachments
            foreach (var a in page.Attachments)
            {
                db.FileStorage.Delete(a.FileId);
            }

            if (coll.Delete(id))
            {
                _cache.Remove(AllPagesKey);
                return (true, null);
            }

            _logger.LogWarning($"Somehow we cannot delete page id {id} and it's a mistery why.");
            return (false, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    // Return null if file cannot be found.
    public (LiteFileInfo<string> meta, byte[] file)? GetFile(string fileId)
    {
        using var db = new LiteDatabase(GetDbPath());

        var meta = db.FileStorage.FindById(fileId);
        if (meta is not object)
            return null;

        using var stream = new MemoryStream();
        db.FileStorage.Download(fileId, stream);
        return (meta, stream.ToArray());
    }
}

record Page
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime LastModifiedUtc { get; set; }

    public List<Attachment> Attachments { get; set; } = new();
}

record Attachment
(
    string FileId,

    string FileName,

    string MimeType,

    DateTime LastModifiedUtc
);

record PageInput(int? Id, string Name, string Content, IFormFile? Attachment)
{
    public static PageInput From(IFormCollection form)
    {
        var (id, name, content) = (form["Id"], form["Name"], form["Content"]);

        int? pageId = null;

        if (!StringValues.IsNullOrEmpty(id))
            pageId = Convert.ToInt32(id);

        IFormFile? file = form.Files["Attachment"];

        return new PageInput(pageId, name!, content!, file);
    }
}

class PageInputValidator : AbstractValidator<PageInput>
{
    public PageInputValidator(string pageName, string homePageName)
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
        if (pageName.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            RuleFor(x => x.Name).Must(name => name.Equals(homePageName)).WithMessage($"You cannot modify home page name. Please keep it {homePageName}");

        RuleFor(x => x.Content).NotEmpty().WithMessage("Content is required");
    }
}