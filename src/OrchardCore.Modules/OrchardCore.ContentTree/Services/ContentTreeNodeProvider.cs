using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Localization;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Metadata.Models;
using OrchardCore.ContentManagement.Metadata.Settings;
using OrchardCore.ContentManagement.Records;
using OrchardCore.ContentTree.Models;
using OrchardCore.ContentTree.ViewModels;
using YesSql;
using YesSql.Services;

namespace OrchardCore.ContentTree.Services
{
    public class ContentTreeNodeProvider : ITreeNodeProvider
    {
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly IContentManager _contentManager;
        private readonly IUrlHelper _urlHelper;
        private readonly ISession _session;
        private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _httpContextAccessor;
        private readonly IAuthorizationService _authorizationService;

        public ContentTreeNodeProvider(
            IContentDefinitionManager contentDefinitionManager,
            IContentManager contentManager,
            IUrlHelperFactory urlHelperFactory,
            IActionContextAccessor actionContextAccessor,
            ISession session,
            IAuthorizationService authorizationService,
            Microsoft.AspNetCore.Http.IHttpContextAccessor httpContextAccessor,
            IStringLocalizer<ContentTreeNodeProvider> stringLocalizer)
        {
            _contentDefinitionManager = contentDefinitionManager;
            _contentManager = contentManager;
            _session = session;
            _httpContextAccessor = httpContextAccessor;
            _authorizationService = authorizationService;

            var ac = actionContextAccessor.ActionContext;
            _urlHelper = urlHelperFactory.GetUrlHelper(ac);


            T = stringLocalizer;
        }

        private readonly IStringLocalizer<ContentTreeNodeProvider> T;

        // todo: not needed? can we rely on the name and id of the root node?
        public string Name => T["Content Types"];
        public string Id => T["content-types"];


        public IEnumerable<TreeNode> GetChildren(string nodeType, string nodeId)
        {
            switch (nodeType)
            {
                case "root":
                    return new[] {
                        GetContentTypesNode()
                    };
                case "content-types":
                    // todo: see if it would be better to use ".Listable()"
                    return _contentDefinitionManager.ListTypeDefinitions()
                        .Where(ctd => ctd.Settings.ToObject<ContentTypeSettings>().Creatable)
                        .OrderBy(ctd => ctd.DisplayName)
                        .Select(GetContentTypeNode).ToArray();
            }

            return new TreeNode[0];
        }


        private TreeNode GetContentTypeNode(ContentTypeDefinition definition)
        {
            return new TreeNode
            {
                Title = definition.DisplayName,
                Type = "content-type",
                Id = definition.Name,
                IsLeaf = true,
                Url = _urlHelper.Action(
                    "GetContentItems", "Admin",
                    new RouteValueDictionary
                    {
                        {"Area", "OrchardCore.ContentTree"},
                        {"Controller", "Admin"},
                        {"Action", "GetContentItems"},
                        {"providerId", Id  },
                        {"providerParams[typename]", definition.Name}
                    })
            };
        }

        private TreeNode GetContentTypesNode()
        {
            return new TreeNode
            {
                Title = Name,
                Type = Id,
                Id = Id
            };
        }

        public TreeNode Get(string nodeType, string nodeId)
        {
            throw new NotImplementedException("Get is not implemented: ContentTreeNodeProvider");
        }

        public async Task<IEnumerable<ContentItem>> GetContentItems(
                Dictionary<string, string> specificParams, 
                CommonContentTreeParams commonParams)
        {
            var query = _session.Query<ContentItem, ContentItemIndex>();


            if ((specificParams != null) && (specificParams.ContainsKey("typename")) && (specificParams["typename"] != null))
            {
                var typeName = specificParams["typename"];
                var contentTypeDefinition = _contentDefinitionManager.GetTypeDefinition(typeName);
                if (contentTypeDefinition == null)
                    throw new System.ArgumentException($"The content type {typeName} does not exist.");

                query = query.With<ContentItemIndex>(x => x.ContentType == typeName);
            }
            else
            {
                var listableTypes = (await GetListableTypesAsync()).Select(t => t.Name).ToArray();
                if (listableTypes.Any())
                {
                    query = query.With<ContentItemIndex>(x => x.ContentType.IsIn(listableTypes));
                }
            }

            query = ApplyCommonParametersToQuery(query, commonParams);

            return await query.ListAsync();
        }


        private IQuery<ContentItem, ContentItemIndex> ApplyCommonParametersToQuery(
                               IQuery<ContentItem, ContentItemIndex> query,
                               CommonContentTreeParams commonParams)
        {

            if (query == null)
            {
                throw new System.ArgumentNullException(nameof(query));
            }

            if (commonParams == null)
            {
                return query;
            }
            


            switch (commonParams.ContentStatusFilter)
            {
                case ContentsStatusFilter.Published:
                    query = query.With<ContentItemIndex>(x => x.Published);
                    break;
                case ContentsStatusFilter.Draft:
                    query = query.With<ContentItemIndex>(x => x.Latest && !x.Published);
                    break;
                case ContentsStatusFilter.AllVersions:
                    query = query.With<ContentItemIndex>(x => x.Latest);
                    break;
                default:
                    query = query.With<ContentItemIndex>(x => x.Latest);
                    break;
            }

            if (commonParams.OwnedByMe)
            {
                var UserName = _httpContextAccessor.HttpContext?.User.Identity.Name;
                query = query.With<ContentItemIndex>(x => x.Owner == UserName);
            }

            switch (commonParams.SortBy)
            {
                case ContentsOrder.Modified:
                    query = commonParams.SortDirection == SortDirection.Ascending ?
                                    query.OrderBy(x => x.ModifiedUtc) : query.OrderByDescending(x => x.ModifiedUtc);
                    break;
                case ContentsOrder.Published:
                    query = commonParams.SortDirection == SortDirection.Ascending ?
                                    query.OrderBy(x => x.PublishedUtc) : query.OrderByDescending(x => x.PublishedUtc);
                    break;
                case ContentsOrder.Created:
                    query = commonParams.SortDirection == SortDirection.Ascending ?
                                    query.OrderBy(x => x.CreatedUtc) : query.OrderByDescending(x => x.CreatedUtc);
                    break;
                default:
                    query = commonParams.SortDirection == SortDirection.Ascending ?
                                    query.OrderBy(x => x.ModifiedUtc) : query.OrderByDescending(x => x.ModifiedUtc);
                    break;
            }

            return query;
        }

        // todo: this should be available on a central location, it is a common requirement
        private async Task<IEnumerable<ContentTypeDefinition>> GetListableTypesAsync()
        {
            var listable = new List<ContentTypeDefinition>();

            var user = _httpContextAccessor.HttpContext?.User;
            if (user == null)
            {
                return listable;
            }

            foreach (var ctd in _contentDefinitionManager.ListTypeDefinitions())
            {
                if (ctd.Settings.ToObject<ContentTypeSettings>().Listable)
                {
                    var authorized = await _authorizationService.AuthorizeAsync(user, Contents.Permissions.EditContent, await _contentManager.NewAsync(ctd.Name));
                    if (authorized)
                    {
                        listable.Add(ctd);
                    }
                }
            }
            return listable;

        }



    }
}
