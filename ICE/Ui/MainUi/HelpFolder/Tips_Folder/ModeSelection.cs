using Dalamud.Interface;
using System;
using System.Collections.Generic;
using System.Text;

namespace ICE.Ui.MainUi.HelpFolder.Tips_Folder
{
    internal class ModeSelection
    {
        public static void Draw()
        {
            ImGui.TextWrapped(Loc.T("There is 5 different modes that exist currently (as of writing this) that all serve minorly differently functions."));
            ImGui.TextWrapped(Loc.T("Depending on what you want / what your goal is, these all serve all different functions."));

            if (ImGui.BeginTabBar("Mode Selection Info"))
            {
                if (ImGui.BeginTabItem(Loc.T("Standard")))
                {
                    StandardMode();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Loc.T("Relic Grind")))
                {
                    RelicGrind();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Loc.T("Leveling Mode")))
                {
                    LevelingMode();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Loc.T("Gold Completion Grind")))
                {
                    GoldCompletion();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Loc.T("Agenda Mode")))
                {
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        private static void StandardMode()
        {
            ImGui.Dummy(new(0, 5));
            ImGuiEx.IconWithText(FontAwesomeIcon.List, Loc.T("Standard"));
            ImGui.TextWrapped(
                Loc.T("The most straightforward mode. Standard runs only the missions you have enabled " +
                "for your current class — you pick what you want done, and it handles the rest."));
            ImGui.TextWrapped(Loc.T("This gives you full control over what missions to run, making it ideal for:"));
            ImGui.BulletText(Loc.T("Score Farming — select specific high-value missions"));
            ImGui.BulletText(Loc.T("Exp Grinding"));
            ImGui.BulletText(Loc.T("Credit / Planetary Credits / Token farming"));
            ImGui.TextWrapped(
                Loc.T("Basic missions (Ranks D→A) will only pull from the class you started on. " +
                "Provisional missions (Weather, Timed, and Sequence) and Red Alerts will be picked up " +
                "between missions — enable the multi-class setting to allow switching classes for these as well."));
        }

        private static void RelicGrind()
        {
            ImGui.Dummy(new(0, 5));
            ImGuiEx.IconWithText(FontAwesomeIcon.ArrowUpRightDots, Loc.T("Relic Grind"));

            ImGui.TextWrapped(
                Loc.T("A mode designed to automate mission selection for relic progression with minimal intervention. " +
                "It scans all available missions, evaluates the experience each one provides, and picks whichever " +
                "yields the most for your current level."));
            ImGui.TextWrapped(
                Loc.T("If you're high enough level to need the next rank category but haven't unlocked it yet " +
                "(e.g. Rank D completed but Rank C not yet unlocked), make sure to unlock it manually before starting."));
            ImGui.TextWrapped(Loc.T("Note: this mode does not swap planets for you. Each planet also has an exp cap:"));
            ImGui.BulletText(Loc.T("Sinus   — Rank IV Max"));
            ImGui.BulletText(Loc.T("Phaenna — Rank V Max"));
            ImGui.BulletText(Loc.T("Oizys   — Rank VI Max"));
            ImGui.BulletText(Loc.T("Auxesia - Rank VII Max"));
            ImGui.TextWrapped(Loc.T("So make sure that you're on the correct planet to accomodate for the exp that you need" +
                "and to allow for completion your relic."));
            ImGui.Dummy(new(0, 5));
            ImGui.TextWrapped(Loc.T("When the tool's analysis reaches the required value, the plugin returns to the hub, swaps to another job, upgrades the tool at Researchingway, swaps back, equips the best gear and resumes (Mission Settings: \"Relic Mode: Auto Upgrade Tool\")."));
            ImGui.TextWrapped(Loc.T("D/C/B rank missions you have never completed are taken first so the next rank keeps unlocking (Mission Settings: \"Relic Mode: Prioritize Incomplete D-B\")."));
        }

        public static void GoldCompletion()
        {
            ImGui.Dummy(new(0, 5));
            ImGuiEx.IconWithText(FontAwesomeIcon.Trophy, Loc.T("Gold Completion"));

            ImGui.TextWrapped(
                Loc.T("A very direct mode of aiming to get a gold completion of every single mission that is not currently gold-completed." +
                "Removed the need of selecting each mission that you want to do, and will automatically pick->choose the missions based on" +
                "the priority that you currently have set."));
            ImGui.TextWrapped(Loc.T("PLEASE NOTE: that this has no other internal logic. It has no way to tell it can't do the mission due to either" +
                "a set of missing stats, no food... ect. This is just meant to be the most direct \"auto select missions that it can do\"." +
                "If you want some control over WHICH missions that you know you can do, select standard mode and choose the missions that need completed"));
            ImGui.TextWrapped(Loc.T("This mode also respects the settings of being able to grind off class provisionals"));

        }

        public static void LevelingMode()
        {
            ImGui.Dummy(new(0, 5));
            ImGuiEx.IconWithText(FontAwesomeIcon.Leaf, Loc.T("Leveling Mode"));

            ImGui.TextWrapped(Loc.T("A mode designed around selecting the best missions that give both the most experience, while also" +
                "choosing the missions that can be done the quickest. These are all completed on bronze completion (aka the fastest you can complete a mission)" +
                "because experience doesn't scale off of the level of turnin. Meaning if a mission gives 125% exp, it'll alwayws give that"));

            ImGui.Dummy(new(0, 5));
            ImGui.Text(Loc.T("Crafters"));
            ImGui.BulletText(Loc.T("Missions are selected with the lowest progress"));
            ImGui.BulletText(Loc.T("Quality DOES NOT MATTER for these"));
            ImGui.BulletText(Loc.T("YOU WILL NEED TO GO UNLOCK COLLECTABLES IN MOR DHONA AT LV. 50 IF YOU HAVEN'T ALREADY"));
            ImGui.BulletText(Loc.T("You can get away with leveling up your gear at the following levels if you really wanna be stingy like me:"));
            ImGui.BulletText(Loc.T("Lv. 10 -> 35 -> 52 -> 80"));
            ImGui.BulletText(Loc.T("These are the points where I found i could just get away with, if you want to make it go faster absolutely can grab gear more often between but."));

            ImGui.Dummy(new(0, 5));
            ImGui.Text(Loc.T("Gathering"));
            ImGui.BulletText(Loc.T("A bit more tedious, and defitenly not the fastest, but it's the safest so far."));
            ImGui.BulletText(Loc.T("Fisher has profiles already built into the plugin, and Btn/Min will auto set profiles to be able to turnin missions ASAP"));
            ImGui.BulletText(Loc.T("You NEED to get gear more often here than crafters, about every 5-7 levels below lv 50, then about every 3 levels after"));
        }

        public static void CosmicAgenda()
        {
            ImGui.Dummy(new(0, 5));
            ImGuiEx.IconWithText(FontAwesomeIcon.ClipboardList, Loc.T("Cosmic Agenda | Agenda Mode"));

            ImGui.TextWrapped(Loc.T("The mode to help combine (most) of the other modes into one little playlist so you can set and forget." +
                "The purpose of this is to allow you to organize when and what order you want to do things"));
            ImGui.TextWrapped(Loc.T("This includes but not limited to:"));
            ImGui.BulletText(Loc.T("Leveling selected classes to to a specific level"));
            ImGui.BulletText(Loc.T("Farming specific classes scores to 500k"));
            ImGui.BulletText(Loc.T("Completed relics on all classes"));
            ImGui.TextWrapped(Loc.T("You can specify the modes that you want to use these from, and it will run continue attempting to do that class until that objective is complete." +
                "This will respect any setting that you currently have enabled, it's jsut a fancy way of letting you the user choose what to do"));
        }
    }
}
