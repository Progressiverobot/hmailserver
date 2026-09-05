-- Referential integrity (roadmap: schema 6030). Orphans first - a row whose parent is gone is

-- exactly what the constraint below exists to prevent, and it would block the constraint.

delete from hm_messageindexterms where mitmessageid not in (select messageid from hm_messages)

delete from hm_imapexpunged where expungedfolderid not in (select folderid from hm_imapfolders)

delete from hm_message_metadata where metadata_messageid not in (select messageid from hm_messages)

delete from hm_messagerecipients where recipientmessageid not in (select messageid from hm_messages)

delete from hm_passwordhistory where phaccountid not in (select accountid from hm_accounts)

delete from hm_group_members where membergroupid not in (select groupid from hm_groups)

delete from hm_rule_actions where actionruleid not in (select ruleid from hm_rules)

delete from hm_rule_criterias where criteriaruleid not in (select ruleid from hm_rules)

delete from hm_apppasswords where apaccountid not in (select accountid from hm_accounts)

delete from hm_fetchaccounts_uids where uidfaid not in (select faid from hm_fetchaccounts)

delete from hm_fetchaccounts where faaccountid not in (select accountid from hm_accounts)

delete from hm_routeaddresses where routeaddressrouteid not in (select routeid from hm_routes)

delete from hm_distributionlistsrecipients where distributionlistrecipientlistid not in (select distributionlistid from hm_distributionlists)

delete from hm_distributionlists where distributionlistdomainid not in (select domainid from hm_domains)

delete from hm_domain_aliases where dadomainid not in (select domainid from hm_domains)

delete from hm_aliases where aliasdomainid not in (select domainid from hm_domains)

delete from hm_accounts where accountdomainid not in (select domainid from hm_domains)



ALTER TABLE hm_accounts DROP CONSTRAINT IF EXISTS fk_hm_accounts_domain

ALTER TABLE hm_accounts WITH CHECK ADD CONSTRAINT fk_hm_accounts_domain FOREIGN KEY (accountdomainid) REFERENCES hm_domains (domainid) ON DELETE CASCADE

ALTER TABLE hm_aliases DROP CONSTRAINT IF EXISTS fk_hm_aliases_domain

ALTER TABLE hm_aliases WITH CHECK ADD CONSTRAINT fk_hm_aliases_domain FOREIGN KEY (aliasdomainid) REFERENCES hm_domains (domainid) ON DELETE CASCADE

ALTER TABLE hm_domain_aliases DROP CONSTRAINT IF EXISTS fk_hm_domain_aliases_domain

ALTER TABLE hm_domain_aliases WITH CHECK ADD CONSTRAINT fk_hm_domain_aliases_domain FOREIGN KEY (dadomainid) REFERENCES hm_domains (domainid) ON DELETE CASCADE

ALTER TABLE hm_distributionlists DROP CONSTRAINT IF EXISTS fk_hm_distributionlists_domain

ALTER TABLE hm_distributionlists WITH CHECK ADD CONSTRAINT fk_hm_distributionlists_domain FOREIGN KEY (distributionlistdomainid) REFERENCES hm_domains (domainid) ON DELETE CASCADE

ALTER TABLE hm_distributionlistsrecipients DROP CONSTRAINT IF EXISTS fk_hm_dlrecipients_list

ALTER TABLE hm_distributionlistsrecipients WITH CHECK ADD CONSTRAINT fk_hm_dlrecipients_list FOREIGN KEY (distributionlistrecipientlistid) REFERENCES hm_distributionlists (distributionlistid) ON DELETE CASCADE

ALTER TABLE hm_routeaddresses DROP CONSTRAINT IF EXISTS fk_hm_routeaddresses_route

ALTER TABLE hm_routeaddresses WITH CHECK ADD CONSTRAINT fk_hm_routeaddresses_route FOREIGN KEY (routeaddressrouteid) REFERENCES hm_routes (routeid) ON DELETE CASCADE

ALTER TABLE hm_fetchaccounts DROP CONSTRAINT IF EXISTS fk_hm_fetchaccounts_account

ALTER TABLE hm_fetchaccounts WITH CHECK ADD CONSTRAINT fk_hm_fetchaccounts_account FOREIGN KEY (faaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE

ALTER TABLE hm_fetchaccounts_uids DROP CONSTRAINT IF EXISTS fk_hm_fetchaccounts_uids_fa

ALTER TABLE hm_fetchaccounts_uids WITH CHECK ADD CONSTRAINT fk_hm_fetchaccounts_uids_fa FOREIGN KEY (uidfaid) REFERENCES hm_fetchaccounts (faid) ON DELETE CASCADE

ALTER TABLE hm_apppasswords DROP CONSTRAINT IF EXISTS fk_hm_apppasswords_account

ALTER TABLE hm_apppasswords WITH CHECK ADD CONSTRAINT fk_hm_apppasswords_account FOREIGN KEY (apaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE

ALTER TABLE hm_rule_criterias DROP CONSTRAINT IF EXISTS fk_hm_rule_criterias_rule

ALTER TABLE hm_rule_criterias WITH CHECK ADD CONSTRAINT fk_hm_rule_criterias_rule FOREIGN KEY (criteriaruleid) REFERENCES hm_rules (ruleid) ON DELETE CASCADE

ALTER TABLE hm_rule_actions DROP CONSTRAINT IF EXISTS fk_hm_rule_actions_rule

ALTER TABLE hm_rule_actions WITH CHECK ADD CONSTRAINT fk_hm_rule_actions_rule FOREIGN KEY (actionruleid) REFERENCES hm_rules (ruleid) ON DELETE CASCADE

ALTER TABLE hm_group_members DROP CONSTRAINT IF EXISTS fk_hm_group_members_group

ALTER TABLE hm_group_members WITH CHECK ADD CONSTRAINT fk_hm_group_members_group FOREIGN KEY (membergroupid) REFERENCES hm_groups (groupid) ON DELETE CASCADE

ALTER TABLE hm_passwordhistory DROP CONSTRAINT IF EXISTS fk_hm_passwordhistory_account

ALTER TABLE hm_passwordhistory WITH CHECK ADD CONSTRAINT fk_hm_passwordhistory_account FOREIGN KEY (phaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE

ALTER TABLE hm_messagerecipients DROP CONSTRAINT IF EXISTS fk_hm_messagerecipients_message

ALTER TABLE hm_messagerecipients WITH CHECK ADD CONSTRAINT fk_hm_messagerecipients_message FOREIGN KEY (recipientmessageid) REFERENCES hm_messages (messageid) ON DELETE CASCADE

ALTER TABLE hm_message_metadata DROP CONSTRAINT IF EXISTS fk_hm_message_metadata_message

ALTER TABLE hm_message_metadata WITH CHECK ADD CONSTRAINT fk_hm_message_metadata_message FOREIGN KEY (metadata_messageid) REFERENCES hm_messages (messageid) ON DELETE CASCADE

ALTER TABLE hm_imapexpunged DROP CONSTRAINT IF EXISTS fk_hm_imapexpunged_folder

ALTER TABLE hm_imapexpunged WITH CHECK ADD CONSTRAINT fk_hm_imapexpunged_folder FOREIGN KEY (expungedfolderid) REFERENCES hm_imapfolders (folderid) ON DELETE CASCADE

ALTER TABLE hm_messageindexterms DROP CONSTRAINT IF EXISTS fk_hm_messageindexterms_message

ALTER TABLE hm_messageindexterms WITH CHECK ADD CONSTRAINT fk_hm_messageindexterms_message FOREIGN KEY (mitmessageid) REFERENCES hm_messages (messageid) ON DELETE CASCADE

update hm_dbversion set value = 6030
