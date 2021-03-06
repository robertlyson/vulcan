﻿namespace TcbInternetSolutions.Vulcan.Core.SearchProviders
{
    using EPiServer;
    using EPiServer.Core;
    using EPiServer.Core.Html;
    using EPiServer.DataAbstraction;
    using EPiServer.Framework;
    using EPiServer.Framework.Localization;
    using EPiServer.Globalization;
    using EPiServer.Shell;
    using EPiServer.Shell.Search;
    using EPiServer.Shell.Web.Mvc.Html;
    using EPiServer.SpecializedProperties;
    using EPiServer.Web;
    using Extensions;
    using Implementation;
    using Nest;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Web;
    using TcbInternetSolutions.Vulcan.Core.Extensions;

    /// <summary>
    /// Base class for UI search providers
    /// </summary>
    /// <typeparam name="TContent"></typeparam>
    public abstract class VulcanSearchProviderBase<TContent> :
        ISearchProvider, ISortable where TContent : class, IContent
    {
        /// <summary>
        /// Link for the search hit, which should be a link to the edit page for the content.
        /// </summary>
        public Func<IContent, ContentReference, string, string> EditPath;

        /// <summary>
        /// Content repository
        /// </summary>
        protected IContentRepository _ContentRepository;

        /// <summary>
        /// Content type repository
        /// </summary>
        protected IContentTypeRepository _ContentTypeRepository;

        /// <summary>
        /// Site definition resolver
        /// </summary>
        protected ISiteDefinitionResolver _SiteDefinitionResolver;

        /// <summary>
        /// Localization service
        /// </summary>
        protected LocalizationService _LocalizationService;

        /// <summary>
        /// UI descriptor registry
        /// </summary>
        protected UIDescriptorRegistry _UIDescriptorRegistry;

        /// <summary>
        /// Vulcan handler
        /// </summary>
        protected IVulcanHandler _VulcanHandler;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="vulcanHandler"></param>
        /// <param name="contentRepository"></param>
        /// <param name="contentTypeRepository"></param>
        /// <param name="localizationService"></param>
        /// <param name="uiDescriptorRegistry"></param>
        /// <param name="enterpriseSettings"></param>
        public VulcanSearchProviderBase(IVulcanHandler vulcanHandler, IContentRepository contentRepository, IContentTypeRepository contentTypeRepository, LocalizationService localizationService, UIDescriptorRegistry uiDescriptorRegistry, ISiteDefinitionResolver enterpriseSettings)
        {
            _VulcanHandler = vulcanHandler;
            _ContentRepository = contentRepository;
            _ContentTypeRepository = contentTypeRepository;
            _LocalizationService = localizationService;
            _UIDescriptorRegistry = uiDescriptorRegistry;
            _SiteDefinitionResolver = enterpriseSettings;

            EditPath = (contentData, contentLink, languageName) =>
            {
                var uri = SearchProviderExtensions.GetUri(contentData);

                if (!string.IsNullOrWhiteSpace(languageName))
                    return string.Format("{0}#language={1}", uri, languageName);

                return uri;
            };
        }

        /// <summary>
        /// UI search area
        /// </summary>
        public abstract string Area { get; }

        /// <summary>
        /// UI search category
        /// </summary>
        public abstract string Category { get; }

        /// <summary>
        /// Sort order
        /// </summary>
        public virtual int SortOrder => 99;

        /// <summary>
        /// Include invariant results
        /// </summary>
        public virtual bool IncludeInvariant => false;

        /// <summary>
        /// The root path to the tool tip resource for the content search provider
        /// </summary>
        protected virtual string ToolTipResourceKeyBase => null;

        /// <summary>
        /// The tool tip key for the content type name.
        /// </summary>        
        protected virtual string ToolTipContentTypeNameResourceKey => null;

        /// <summary>
        /// Search
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        public virtual IEnumerable<SearchResult> Search(Query query)
        {
            List<ContentReference> searchRoots = null;
            var searchText = query.SearchQuery;

            if (query.SearchRoots?.Any() == true)
            {
                searchRoots = new List<ContentReference>();
                ContentReference c = null;

                foreach (var item in query.SearchRoots)
                {
                    if (ContentReference.TryParse(item, out c))
                        searchRoots.Add(c);
                }
            }

            List<Type> typeRestriction = typeof(TContent).GetSearchTypesFor(VulcanFieldConstants.DefaultFilter);

            // Special condition for BlockData since it doesn't derive from BlockData
            if (typeof(TContent) == typeof(VulcanContentHit))
            {
                typeRestriction = typeof(BlockData).GetSearchTypesFor(VulcanFieldConstants.DefaultFilter);
            }

            var hits = new List<ISearchResponse<IContent>>();

            var clients = _VulcanHandler.GetClients();

            if (clients != null)
            {
                foreach (var client in clients)
                {
                    if (client.Language != CultureInfo.InvariantCulture || IncludeInvariant)
                    {
                        var clientHits = client.SearchContent<IContent>(d => d
                                .Take(query.MaxResults)
                                .Fields(fs => fs.Field(p => p.ContentLink)) // only return id for performance
                                .Query(x =>
                                    x.SimpleQueryString(sqs =>
                                        sqs.Fields(f => f
                                                    .AllAnalyzed()
                                                    .Field($"{VulcanFieldConstants.MediaContents}.content")
                                                    .Field($"{VulcanFieldConstants.MediaContents}.content_type"))
                                                .Query(searchText))
                                ),
                                includeNeutralLanguage: client.Language == CultureInfo.InvariantCulture,
                                rootReferences: searchRoots,
                                typeFilter: typeRestriction,
                                principleReadFilter: UserExtensions.GetUser()
                        );

                        if (clientHits != null && clientHits.Total > 0)
                        {
                            hits.Add(clientHits);
                        }
                    }
                }
            }

            var results = hits.SelectMany(h => h.Hits.Select(x => CreateSearchResult(x)));

            return results;
        }

        /// <summary>
        /// Creates a preview text from a PageData. Will first look for the property MainIntro, and if that's missing, a property called MainBody.
        /// </summary>
        /// <param name="content">The page to extract the preview from.</param>
        protected virtual string CreatePreviewText(IContentData content)
        {
            string str = string.Empty;

            if (content == null)
                return str;

            return TextIndexer.StripHtml(content.Property["MainIntro"] == null ?
                (content.Property["MainBody"] == null ?
                    GetPreviewTextFromFirstLongString(content) : content.Property["MainBody"].ToWebString()) : content.Property["MainIntro"].ToWebString(), 200);
        }

        /// <summary>
        /// Builds result search information for IContent
        /// </summary>
        /// <param name="searchHit"></param>
        /// <returns></returns>
        protected virtual SearchResult CreateSearchResult(IHit<IContent> searchHit)
        {
            Validator.ThrowIfNull(nameof(searchHit), searchHit);

            // load the content from the given link
            var referenceString = (searchHit.Fields["contentLink"] as JArray)?.FirstOrDefault();
            ContentReference reference = null;

            if (referenceString != null)
                ContentReference.TryParse(referenceString.ToString(), out reference);

            if (ContentReference.IsNullOrEmpty(reference))
                throw new Exception("Unable to convert search hit to IContent!");

            var content = _ContentRepository.Get<IContent>(reference);
            ILocalizable localizable = content as ILocalizable;
            IChangeTrackable changeTracking = content as IChangeTrackable;

            bool onCurrentHost;
            SearchResult result = new SearchResult
            (
                GetEditUrl(content, out onCurrentHost),
                HttpUtility.HtmlEncode(content.Name),
                CreatePreviewText(content)
            );

            result.IconCssClass = IconCssClass(content);
            result.Metadata["Id"] = content.ContentLink.ToString();
            result.Metadata["LanguageBranch"] = localizable == null || localizable.Language == null ? string.Empty : localizable.Language.Name;
            result.Metadata["ParentId"] = content.ParentLink.ToString();
            result.Metadata["IsOnCurrentHost"] = onCurrentHost ? "true" : "false";
            result.Metadata["TypeIdentifier"] = SearchProviderExtensions.GetTypeIdentifier(content, _UIDescriptorRegistry);
            ContentType contentType = _ContentTypeRepository.Load(content.ContentTypeID);

            CreateToolTip(content, changeTracking, result, contentType);
            result.Language = localizable == null || localizable.Language == null ? string.Empty : localizable.Language.NativeName;

            return result;
        }

        /// <summary>
        /// Gets the edit URL for a <see cref="T:EPiServer.Core.IContent"/>.
        /// </summary>
        /// <param name="contentData">The content data.</param><param name="onCurrentHost">if set to <c>true</c> current host are used.</param>
        /// <returns>
        /// The edit url.
        /// </returns>
        protected virtual string GetEditUrl(IContent contentData, out bool onCurrentHost)
        {
            ContentReference contentLink = contentData.ContentLink;
            ILocalizable localizable = contentData as ILocalizable;
            string language = localizable != null ? localizable.Language.Name : ContentLanguage.PreferredCulture.Name;
            string editUrl = EditPath(contentData, contentLink, language);
            onCurrentHost = true;
            SiteDefinition definitionForContent = _SiteDefinitionResolver.GetByContent(contentData.ContentLink, true, true);

            if (definitionForContent?.SiteUrl != SiteDefinition.Current.SiteUrl)
                onCurrentHost = false;

            //if (Settings.Instance.UseLegacyEditMode && typeof(PageData).IsAssignableFrom(typeof(TContent)))
            //    return UriSupport.Combine(UriSupport.Combine(definitionForContent.SiteUrl, settingsFromContent.UIUrl).AbsoluteUri, editUrl);

            return editUrl;
        }

        /// <summary>
        /// Will look for the first long string property, ignoring link collections, that has a value.
        /// </summary>
        /// <param name="content">The page that we want to get a preview for.</param>
        /// <returns>
        /// The value from the first non empty long string.
        /// </returns>
        protected virtual string GetPreviewTextFromFirstLongString(IContentData content)
        {
            foreach (PropertyData propertyData in content.Property)
            {
                if (propertyData is PropertyLongString && !(propertyData is PropertyLinkCollection) && !string.IsNullOrEmpty(propertyData.Value as string))
                    return propertyData.ToWebString();
            }

            return string.Empty;
        }

        /// <summary>
        /// Gets the icon CSS class.
        /// </summary>
        protected abstract string IconCssClass(IContent contentData);

        private void CreateToolTip(IContent content, IChangeTrackable changeTracking, SearchResult result, ContentType contentType)
        {
            if (string.IsNullOrEmpty(ToolTipResourceKeyBase))
                return;

            result.ToolTipElements.Add(new ToolTipElement(_LocalizationService.GetString(string.Format(CultureInfo.InvariantCulture, "{0}/id", new object[1]
            {
                ToolTipResourceKeyBase
            })), content.ContentLink.ToString()));

            if (changeTracking != null)
            {
                result.ToolTipElements.Add(new ToolTipElement(_LocalizationService.GetString(string.Format(CultureInfo.InvariantCulture, "{0}/changed", new object[1]
                {
                    ToolTipResourceKeyBase
                })), changeTracking.Changed.ToString()));

                result.ToolTipElements.Add(new ToolTipElement(_LocalizationService.GetString(string.Format(CultureInfo.InvariantCulture, "{0}/created", new object[1]
                {
                    ToolTipResourceKeyBase
                })), changeTracking.Created.ToString()));
            }

            if (string.IsNullOrEmpty(ToolTipContentTypeNameResourceKey))
                return;

            result.ToolTipElements.Add
                (new ToolTipElement(_LocalizationService.GetString(string.Format(CultureInfo.InvariantCulture, "{0}/{1}", new object[2]
            {
                ToolTipResourceKeyBase,
                ToolTipContentTypeNameResourceKey
            })), contentType != null ? HttpUtility.HtmlEncode(contentType.LocalizedName) : string.Empty));
        }
    }
}