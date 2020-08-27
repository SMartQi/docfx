// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Docs.Build
{
    internal class TableOfContentsLoader
    {
        private readonly LinkResolver _linkResolver;
        private readonly XrefResolver _xrefResolver;
        private readonly TableOfContentsParser _parser;
        private readonly MonikerProvider _monikerProvider;
        private readonly DependencyMapBuilder _dependencyMapBuilder;
        private readonly ContentValidator _contentValidator;
        private readonly ErrorBuilder _errors;
        private readonly DocumentProvider _documentProvider;
        private readonly IReadOnlyDictionary<string, JoinTOCConfig> _joinTOCConfigs;

        private readonly MemoryCache<FilePath, (TableOfContentsNode, List<Document>, List<Document>)> _cache =
                     new MemoryCache<FilePath, (TableOfContentsNode, List<Document>, List<Document>)>();

        private static readonly string[] s_tocFileNames = new[] { "TOC.md", "TOC.json", "TOC.yml" };
        private static readonly string[] s_experimentalTocFileNames = new[] { "TOC.experimental.md", "TOC.experimental.json", "TOC.experimental.yml" };

        private static readonly AsyncLocal<ImmutableStack<Document>> t_recursionDetector =
                            new AsyncLocal<ImmutableStack<Document>> { Value = ImmutableStack<Document>.Empty };

        public TableOfContentsLoader(
            LinkResolver linkResolver,
            XrefResolver xrefResolver,
            TableOfContentsParser parser,
            MonikerProvider monikerProvider,
            DependencyMapBuilder dependencyMapBuilder,
            ContentValidator contentValidator,
            Config config,
            ErrorBuilder errors,
            DocumentProvider documentProvider)
        {
            _linkResolver = linkResolver;
            _xrefResolver = xrefResolver;
            _parser = parser;
            _monikerProvider = monikerProvider;
            _dependencyMapBuilder = dependencyMapBuilder;
            _contentValidator = contentValidator;
            _errors = errors;
            _documentProvider = documentProvider;
            _joinTOCConfigs = config.JoinTOC.Where(x => x.ReferenceToc != null).ToDictionary(x => PathUtility.Normalize(x.ReferenceToc!));
        }

        public (TableOfContentsNode node, List<Document> referencedFiles, List<Document> referencedTocs)
            Load(Document file)
        {
            return _cache.GetOrAdd(file.FilePath, _ =>
            {
                var referencedFiles = new List<Document>();
                var referencedTocs = new List<Document>();
                var node = LoadTocFile(file, file, referencedFiles, referencedTocs);

                if (_joinTOCConfigs.TryGetValue(file.FilePath.Path, out var joinTOCConfig) && joinTOCConfig != null)
                {
                    if (joinTOCConfig.TopLevelToc != null)
                    {
                        node = JoinToc(node, joinTOCConfig.TopLevelToc);
                    }
                }
                return (node, referencedFiles, referencedTocs);
            });
        }

        private TableOfContentsNode JoinToc(TableOfContentsNode referenceToc, string topLevelTocFilePath)
        {
            var (topLevelToc, _, _) = Load(_documentProvider.GetDocument(FilePath.Content(new PathString(topLevelTocFilePath))));
            TraverseAndMerge(topLevelToc, referenceToc.Items, new HashSet<TableOfContentsNode>());
            return topLevelToc;
        }

        private void TraverseAndMerge(TableOfContentsNode node, List<SourceInfo<TableOfContentsNode>> itemsToMatch, HashSet<TableOfContentsNode> matched)
        {
            foreach (var pattern in node.Children)
            {
                foreach (var item in itemsToMatch)
                {
                    if (item.Value.Name != null && !matched.Contains(item) && GlobUtility.CreateGlobMatcher(pattern)(item.Value.Name!))
                    {
                        matched.Add(item);
                        node.Items.Add(item);
                    }
                }
            }

            foreach (var item in node.Items)
            {
                TraverseAndMerge(item, itemsToMatch, matched);
            }
        }

        private TableOfContentsNode LoadTocFile(
            Document file, Document rootPath, List<Document> referencedFiles, List<Document> referencedTocs)
        {
            // add to parent path
            t_recursionDetector.Value ??= ImmutableStack<Document>.Empty;

            var recursionDetector = t_recursionDetector.Value!;
            if (recursionDetector.Contains(file))
            {
                throw Errors.Link.CircularReference(new SourceInfo(file.FilePath, 1, 1), file, recursionDetector).ToException();
            }

            try
            {
                recursionDetector = recursionDetector.Push(file);
                t_recursionDetector.Value = recursionDetector;

                var node = _parser.Parse(file.FilePath, _errors);
                node.Items = LoadTocNodes(node.Items, file, rootPath, referencedFiles, referencedTocs);

                if (file == rootPath)
                {
                    _contentValidator.ValidateTocEntryDuplicated(file.FilePath, referencedFiles);
                }
                return node;
            }
            finally
            {
                t_recursionDetector.Value = recursionDetector.Pop();
            }
        }

        private List<SourceInfo<TableOfContentsNode>> LoadTocNodes(
            List<SourceInfo<TableOfContentsNode>> nodes,
            Document filePath,
            Document rootPath,
            List<Document> referencedFiles,
            List<Document> referencedTocs)
        {
            var newNodes = new SourceInfo<TableOfContentsNode>[nodes.Count];

            Parallel.For(0, nodes.Count, i =>
            {
                var newReferencedFiles = new List<Document>();
                var newReferencedTocs = new List<Document>();
                newNodes[i] = LoadTocNode(nodes[i], filePath, rootPath, newReferencedFiles, newReferencedTocs);
                lock (newNodes)
                {
                    referencedFiles.AddRange(newReferencedFiles);
                    referencedTocs.AddRange(newReferencedTocs);
                }
            });

            return newNodes.ToList();
        }

        private SourceInfo<TableOfContentsNode> LoadTocNode(
            SourceInfo<TableOfContentsNode> node,
            Document filePath,
            Document rootPath,
            List<Document> referencedFiles,
            List<Document> referencedTocs)
        {
            // process
            var tocHref = GetTocHref(node);
            var topicHref = GetTopicHref(node);
            var topicUid = node.Value.Uid;

            _contentValidator.ValidateTocBreadcrumbLinkExternal(filePath.FilePath, node);

            var (resolvedTocHref, subChildren, subChildrenFirstItem, tocHrefType) = ProcessTocHref(
                filePath, rootPath, referencedFiles, referencedTocs, tocHref);
            var (resolvedTopicHref, resolvedTopicName, document) = ProcessTopicItem(
                filePath, rootPath, referencedFiles, topicUid, topicHref, addToReferencedFiles: !IsTocIncludeHref(tocHrefType));

            // resolve children
            var items = subChildren?.Items ?? LoadTocNodes(node.Value.Items, filePath, rootPath, referencedFiles, referencedTocs);

            // set resolved href/document back
            var newNode = new TableOfContentsNode(node)
            {
                Href = resolvedTocHref.Or(resolvedTopicHref).Or(subChildrenFirstItem?.Href),
                TocHref = default,
                TopicHref = default,
                Homepage = string.IsNullOrEmpty(node.Value.Href) && !string.IsNullOrEmpty(node.Value.TopicHref)
                    ? resolvedTopicHref : default,
                Name = node.Value.Name.Or(resolvedTopicName),
                Document = document ?? subChildrenFirstItem?.Document,
                Items = items,
            };

            // resolve monikers
            newNode.Monikers = GetMonikers(newNode);

            // validate
            if (string.IsNullOrEmpty(newNode.Name))
            {
                _errors.Add(Errors.JsonSchema.MissingAttribute(newNode.Name.Source ?? node.Source, "name"));
            }

            return new SourceInfo<TableOfContentsNode>(newNode, node);
        }

        private MonikerList GetMonikers(TableOfContentsNode currentItem)
        {
            var monikers = MonikerList.Union(GetMonikerLists(currentItem));

            foreach (var item in currentItem.Items)
            {
                if (monikers == item.Value.Monikers)
                {
                    item.Value.Monikers = default;
                }
            }
            return monikers;
        }

        private IEnumerable<MonikerList> GetMonikerLists(TableOfContentsNode currentItem)
        {
            if (!string.IsNullOrEmpty(currentItem.Href))
            {
                var linkType = UrlUtility.GetLinkType(currentItem.Href);
                if (linkType == LinkType.External || linkType == LinkType.AbsolutePath)
                {
                    yield return default;
                }
                else if (currentItem.Document != null)
                {
                    yield return _monikerProvider.GetFileLevelMonikers(_errors, currentItem.Document.FilePath);
                }
            }

            // Union with children's monikers
            foreach (var item in currentItem.Items)
            {
                yield return item.Value.Monikers;
            }
        }

        private SourceInfo<string?> GetTocHref(TableOfContentsNode tocInputModel)
        {
            if (!string.IsNullOrEmpty(tocInputModel.TocHref))
            {
                var tocHrefType = GetHrefType(tocInputModel.TocHref);
                if (IsTocIncludeHref(tocHrefType) || tocHrefType == TocHrefType.AbsolutePath)
                {
                    return tocInputModel.TocHref;
                }
                else
                {
                    _errors.AddIfNotNull(Errors.TableOfContents.InvalidTocHref(tocInputModel.TocHref));
                }
            }

            if (!string.IsNullOrEmpty(tocInputModel.Href) && IsTocIncludeHref(GetHrefType(tocInputModel.Href)))
            {
                return tocInputModel.Href;
            }

            return default;
        }

        private SourceInfo<string?> GetTopicHref(TableOfContentsNode tocInputModel)
        {
            if (!string.IsNullOrEmpty(tocInputModel.TopicHref))
            {
                var topicHrefType = GetHrefType(tocInputModel.TopicHref);
                if (IsTocIncludeHref(topicHrefType))
                {
                    _errors.Add(Errors.TableOfContents.InvalidTopicHref(tocInputModel.TopicHref));
                }
                else
                {
                    return tocInputModel.TopicHref;
                }
            }

            if (string.IsNullOrEmpty(tocInputModel.Href) || !IsTocIncludeHref(GetHrefType(tocInputModel.Href)))
            {
                return tocInputModel.Href;
            }

            return default;
        }

        private (SourceInfo<string?> resolvedTocHref, TableOfContentsNode? subChildren, TableOfContentsNode? subChildrenFirstItem, TocHrefType tocHrefType)
            ProcessTocHref(
                Document filePath,
                Document rootPath,
                List<Document> referencedFiles,
                List<Document> referencedTocs,
                SourceInfo<string?> tocHref)
        {
            if (string.IsNullOrEmpty(tocHref))
            {
                return (tocHref, default, default, default);
            }

            var tocHrefType = GetHrefType(tocHref);
            Debug.Assert(tocHrefType == TocHrefType.AbsolutePath || IsTocIncludeHref(tocHrefType));

            if (tocHrefType == TocHrefType.AbsolutePath)
            {
                return (tocHref, default, default, default);
            }

            var (hrefPath, _, _) = UrlUtility.SplitUrl(tocHref.Value ?? "");
            var referenceTocFilePath = ResolveTocHref(filePath, referencedTocs, tocHrefType, new SourceInfo<string>(hrefPath, tocHref));
            if (referenceTocFilePath != null)
            {
                var nestedToc = LoadTocFile(
                    referenceTocFilePath,
                    rootPath,
                    tocHrefType == TocHrefType.RelativeFolder ? new List<Document>() : referencedFiles,
                    referencedTocs);

                if (tocHrefType == TocHrefType.RelativeFolder)
                {
                    var nestedTocFirstItem = GetFirstItem(nestedToc.Items);
                    _dependencyMapBuilder.AddDependencyItem(
                        filePath.FilePath, nestedTocFirstItem?.Document?.FilePath, DependencyType.File, filePath.ContentType);
                    return (default, default, nestedTocFirstItem, tocHrefType);
                }

                return (default, nestedToc, default, tocHrefType);
            }

            return default;
        }

        private (SourceInfo<string?> resolvedTopicHref, SourceInfo<string?> resolvedTopicName, Document? file) ProcessTopicItem(
            Document filePath,
            Document rootPath,
            List<Document> referencedFiles,
            SourceInfo<string?> uid,
            SourceInfo<string?> topicHref,
            bool addToReferencedFiles = true)
        {
            // process href first
            if (!string.IsNullOrEmpty(topicHref))
            {
                var topicHrefType = GetHrefType(topicHref);
                Debug.Assert(topicHrefType == TocHrefType.AbsolutePath || !IsTocIncludeHref(topicHrefType));

                var (error, link, resolvedFile) = _linkResolver.ResolveLink(topicHref!, filePath, rootPath);
                _errors.AddIfNotNull(error);

                if (resolvedFile != null && addToReferencedFiles)
                {
                    // add to referenced document list
                    referencedFiles.Add(resolvedFile);
                }
                return (new SourceInfo<string?>(link, topicHref), default, resolvedFile);
            }

            // process uid then if href is empty or null
            if (!string.IsNullOrEmpty(uid.Value))
            {
                var (uidError, uidLink, display, declaringFile) = _xrefResolver.ResolveXrefByUid(
                    uid!, filePath, rootPath, _monikerProvider.GetFileLevelMonikers(ErrorBuilder.Null, filePath.FilePath));
                _errors.AddIfNotNull(uidError);

                if (declaringFile != null && addToReferencedFiles)
                {
                    referencedFiles.Add(declaringFile);
                }

                if (!string.IsNullOrEmpty(uidLink))
                {
                    return (new SourceInfo<string?>(uidLink, uid), new SourceInfo<string?>(display, uid), declaringFile);
                }
            }

            // if both uid and href are empty or null, return default
            return (topicHref, default, default);
        }

        private Document? ResolveTocHref(
            Document filePath, List<Document> referencedTocs, TocHrefType tocHrefType, SourceInfo<string> href)
        {
            switch (tocHrefType)
            {
                case TocHrefType.RelativeFolder:
                    var result = default(Document);
                    foreach (var name in s_tocFileNames)
                    {
                        var probingHref = new SourceInfo<string>(Path.Combine(href, name), href);
                        var (_, subToc) = _linkResolver.ResolveContent(probingHref, filePath);
                        if (subToc != null)
                        {
                            if (!subToc.FilePath.IsGitCommit)
                            {
                                return subToc;
                            }
                            else if (result is null)
                            {
                                result = subToc;
                            }
                        }
                    }
                    if (result == null)
                    {
                        _errors.Add(Errors.TableOfContents.FileNotFound(href));
                    }
                    return result;

                case TocHrefType.TocFile:
                    var (error, referencedToc) = _linkResolver.ResolveContent(href, filePath);
                    _errors.AddIfNotNull(error);
                    referencedTocs.AddIfNotNull(referencedToc);
                    return referencedToc;

                default:
                    return default;
            }
        }

        private static TableOfContentsNode? GetFirstItem(List<SourceInfo<TableOfContentsNode>> items)
        {
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Value.Href))
                {
                    return item;
                }
            }

            foreach (var item in items)
            {
                return GetFirstItem(item.Value.Items);
            }

            return null;
        }

        private static bool IsTocIncludeHref(TocHrefType tocHrefType)
        {
            return tocHrefType == TocHrefType.TocFile || tocHrefType == TocHrefType.RelativeFolder;
        }

        private static TocHrefType GetHrefType(string? href)
        {
            var linkType = UrlUtility.GetLinkType(href);
            if (linkType == LinkType.AbsolutePath || linkType == LinkType.External)
            {
                return TocHrefType.AbsolutePath;
            }

            var (path, _, _) = UrlUtility.SplitUrl(href ?? "");
            if (path.EndsWith('/') || path.EndsWith('\\'))
            {
                return TocHrefType.RelativeFolder;
            }

            var fileName = Path.GetFileName(path);

            if (s_tocFileNames.Concat(s_experimentalTocFileNames).Any(s => s.Equals(fileName, PathUtility.PathComparison)))
            {
                return TocHrefType.TocFile;
            }

            return TocHrefType.RelativeFile;
        }

        private enum TocHrefType
        {
            None,
            AbsolutePath,
            RelativeFile,
            RelativeFolder,
            TocFile,
        }
    }
}
