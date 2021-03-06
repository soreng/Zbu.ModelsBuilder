﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using Umbraco.Core;
using Umbraco.Core.Configuration;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Services;
using Umbraco.ModelsBuilder.Configuration;
using Umbraco.Web;
using Umbraco.Web.Mvc;
using Umbraco.Web.UI.JavaScript;

namespace Umbraco.ModelsBuilder.Umbraco
{
    /// <summary>
    /// Installs ModelsBuilder into the Umbraco site.
    /// </summary>
    public class ModelsBuilderApplication : ApplicationEventHandler
    {
        protected override void ApplicationStarting(UmbracoApplicationBase umbracoApplication, ApplicationContext applicationContext)
        {
            var config = UmbracoConfig.For.ModelsBuilder();

            if (config.ModelsMode == ModelsMode.PureLive)
                InstallLiveModels(applicationContext.ProfilingLogger);
            else if (config.EnableFactory)
                InstallDefaultModelsFactory();

            // always setup the dashboard
            InstallServerVars();

            // do NOT try to do this when running 7.7.9 or below,
            // as the RenderModelBinder.ModelBindingException does not exist
            if (UmbracoVersion.Current >= new Version(7, 7, 10))
                BindModelBindingException();
        }

        private void BindModelBindingException()
        {
            // always setup the binder errors handler
            RenderModelBinder.ModelBindingException += HandleModelBindingException;
        }

        protected override void ApplicationStarted(UmbracoApplicationBase umbracoApplication, ApplicationContext applicationContext)
        {
            var config = UmbracoConfig.For.ModelsBuilder();

            if (config.Enable)
                FileService.SavingTemplate += FileService_SavingTemplate;

            if (config.ModelsMode.IsLiveNotPure())
                LiveModelsProvider.Install();

            if (config.FlagOutOfDateModels)
                OutOfDateModelsStatus.Install();
        }

        private void InstallDefaultModelsFactory()
        {
            var types = PluginManager.Current.ResolveTypes<PublishedContentModel>();
            var factory = new PublishedContentModelFactory(types);
            PublishedContentModelFactoryResolver.Current.SetFactory(factory);
        }

        private void InstallLiveModels(ProfilingLogger logger)
        {
            var factory = new PureLiveModelFactory(logger);
            PublishedContentModelFactoryResolver.Current.SetFactory(factory);

            // the following would add @using statement in every view so user's don't
            // have to do it - however, then noone understands where the @using statement
            // comes from, and it cannot be avoided / removed --- DISABLED
            //
            /*
            // no need for @using in views
            // note:
            //  we are NOT using the in-code attribute here, config is required
            //  because that would require parsing the code... and what if it changes?
            //  we can AddGlobalImport not sure we can remove one anyways
            var modelsNamespace = Configuration.Config.ModelsNamespace;
            if (string.IsNullOrWhiteSpace(modelsNamespace))
                modelsNamespace = Configuration.Config.DefaultModelsNamespace;
            System.Web.WebPages.Razor.WebPageRazorHost.AddGlobalImport(modelsNamespace);
            */
        }

        private void InstallServerVars()
        {
            // register our url - for the backoffice api
            ServerVariablesParser.Parsing += (sender, serverVars) =>
            {
                if (!serverVars.ContainsKey("umbracoUrls"))
                    throw new Exception("Missing umbracoUrls.");
                var umbracoUrlsObject = serverVars["umbracoUrls"];
                if (umbracoUrlsObject == null)
                    throw new Exception("Null umbracoUrls");
                var umbracoUrls = umbracoUrlsObject as Dictionary<string, object>;
                if (umbracoUrls == null)
                    throw new Exception("Invalid umbracoUrls");

                if (!serverVars.ContainsKey("umbracoPlugins"))
                    throw new Exception("Missing umbracoPlugins.");
                var umbracoPlugins = serverVars["umbracoPlugins"] as Dictionary<string, object>;
                if (umbracoPlugins == null)
                    throw new Exception("Invalid umbracoPlugins");

                if (HttpContext.Current == null) throw new InvalidOperationException("HttpContext is null");
                var urlHelper = new UrlHelper(new RequestContext(new HttpContextWrapper(HttpContext.Current), new RouteData()));

                umbracoUrls["modelsBuilderBaseUrl"] = urlHelper.GetUmbracoApiServiceBaseUrl<ModelsBuilderBackOfficeController>(controller => controller.BuildModels());
                umbracoPlugins["modelsBuilder"] = GetModelsBuilderSettings();
            };
        }

        private Dictionary<string, object> GetModelsBuilderSettings()
        {
            if (ApplicationContext.Current.IsConfigured == false)
                return null;

            var settings = new Dictionary<string, object>
                {
                    {"enabled", UmbracoConfig.For.ModelsBuilder().Enable}
                };

            return settings;
        }

        /// <summary>
        /// Used to check if a template is being created based on a document type, in this case we need to
        /// ensure the template markup is correct based on the model name of the document type
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FileService_SavingTemplate(IFileService sender, Core.Events.SaveEventArgs<Core.Models.ITemplate> e)
        {
            // don't do anything if the factory is not enabled
            // because, no factory = no models (even if generation is enabled)
            if (!UmbracoConfig.For.ModelsBuilder().EnableFactory) return;

            // don't do anything if this special key is not found
            if (!e.AdditionalData.ContainsKey("CreateTemplateForContentType")) return;

            // ensure we have the content type alias
            if (!e.AdditionalData.ContainsKey("ContentTypeAlias"))
                throw new InvalidOperationException("The additionalData key: ContentTypeAlias was not found");

            foreach (var template in e.SavedEntities)
            {
                // if it is in fact a new entity (not been saved yet) and the "CreateTemplateForContentType" key
                // is found, then it means a new template is being created based on the creation of a document type
                if (!template.HasIdentity && template.Content.IsNullOrWhiteSpace())
                {
                    // ensure is safe and always pascal cased, per razor standard
                    // + this is how we get the default model name in Umbraco.ModelsBuilder.Umbraco.Application
                    var alias = e.AdditionalData["ContentTypeAlias"].ToString();
                    var name = template.Name; // will be the name of the content type since we are creating
                    var className = Application.GetClrName(name, alias);

                    var modelNamespace = UmbracoConfig.For.ModelsBuilder().ModelsNamespace;

                    // we do not support configuring this at the moment, so just let Umbraco use its default value
                    //var modelNamespaceAlias = ...;

                    var markup = ViewHelper.GetDefaultFileContent(
                        modelClassName: className,
                        modelNamespace: modelNamespace/*,
                        modelNamespaceAlias: modelNamespaceAlias*/);

                    //set the template content to the new markup
                    template.Content = markup;
                }
            }
        }

        private void HandleModelBindingException(object sender, RenderModelBinder.ModelBindingArgs args)
        {
            var sourceAttr = args.SourceType.Assembly.GetCustomAttribute<ModelsBuilderAssemblyAttribute>();
            var modelAttr = args.ModelType.Assembly.GetCustomAttribute<ModelsBuilderAssemblyAttribute>();

            // if source or model is not a ModelsBuider type...
            if (sourceAttr == null || modelAttr == null)
            {
                // if neither are ModelsBuilder types, give up entirely
                if (sourceAttr == null && modelAttr == null)
                    return;

                // else report, but better not restart (loops?)
                args.Message.Append(" The ");
                args.Message.Append(sourceAttr == null ? "view model" : "source");
                args.Message.Append(" is a ModelsBuilder type, but the ");
                args.Message.Append(sourceAttr != null ? "view model" : "source");
                args.Message.Append(" is not. The application is in an unstable state and should be restarted.");
                return;
            }

            // both are ModelsBuilder types
	        var pureSource = sourceAttr.PureLive;
	        var pureModel = modelAttr.PureLive;

	        if (sourceAttr.PureLive || modelAttr.PureLive)
	        {
	            if (pureSource == false || pureModel == false)
	            {
                    // only one is pure - report, but better not restart (loops?)
	                args.Message.Append(pureSource
	                    ? " The content model is PureLive, but the view model is not."
	                    : " The view model is PureLive, but the content model is not.");
	                args.Message.Append(" The application is in an unstable state and should be restarted.");
	            }
	            else
	            {
                    // both are pure - report, and if different versions, restart
                    // if same version... makes no sense... and better not restart (loops?)
	                var sourceVersion = args.SourceType.Assembly.GetName().Version;
                    var modelVersion = args.ModelType.Assembly.GetName().Version;
	                args.Message.Append(" Both view and content models are PureLive, with ");
	                args.Message.Append(sourceVersion == modelVersion
	                    ? "same version. The application is in an unstable state and should be restarted."
	                    : "different versions. The application is in an unstable state and is going to be restarted.");
	                args.Restart = sourceVersion != modelVersion;
	            }
	        }
        }
    }
}
