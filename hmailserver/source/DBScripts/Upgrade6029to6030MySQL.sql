-- Referential integrity (schema 6030). InnoDB is the engine that enforces a FOREIGN KEY; on any

-- MySQL of this century it is the default, and the ALTER is a no-op where it already is.

ALTER TABLE hm_domains ENGINE=InnoDB;

ALTER TABLE hm_accounts ENGINE=InnoDB;

ALTER TABLE hm_aliases ENGINE=InnoDB;

ALTER TABLE hm_domain_aliases ENGINE=InnoDB;

ALTER TABLE hm_distributionlists ENGINE=InnoDB;

ALTER TABLE hm_distributionlistsrecipients ENGINE=InnoDB;

ALTER TABLE hm_routes ENGINE=InnoDB;

ALTER TABLE hm_routeaddresses ENGINE=InnoDB;

ALTER TABLE hm_fetchaccounts ENGINE=InnoDB;

ALTER TABLE hm_fetchaccounts_uids ENGINE=InnoDB;

ALTER TABLE hm_apppasswords ENGINE=InnoDB;

ALTER TABLE hm_rules ENGINE=InnoDB;

ALTER TABLE hm_rule_criterias ENGINE=InnoDB;

ALTER TABLE hm_rule_actions ENGINE=InnoDB;

ALTER TABLE hm_groups ENGINE=InnoDB;

ALTER TABLE hm_group_members ENGINE=InnoDB;

ALTER TABLE hm_passwordhistory ENGINE=InnoDB;

ALTER TABLE hm_messages ENGINE=InnoDB;

ALTER TABLE hm_messagerecipients ENGINE=InnoDB;

ALTER TABLE hm_message_metadata ENGINE=InnoDB;

ALTER TABLE hm_imapfolders ENGINE=InnoDB;

ALTER TABLE hm_imapexpunged ENGINE=InnoDB;

ALTER TABLE hm_messageindexterms ENGINE=InnoDB;



delete from hm_messageindexterms where mitmessageid not in (select messageid from hm_messages);

delete from hm_imapexpunged where expungedfolderid not in (select folderid from hm_imapfolders);

delete from hm_message_metadata where metadata_messageid not in (select messageid from hm_messages);

delete from hm_messagerecipients where recipientmessageid not in (select messageid from hm_messages);

delete from hm_passwordhistory where phaccountid not in (select accountid from hm_accounts);

delete from hm_group_members where membergroupid not in (select groupid from hm_groups);

delete from hm_rule_actions where actionruleid not in (select ruleid from hm_rules);

delete from hm_rule_criterias where criteriaruleid not in (select ruleid from hm_rules);

delete from hm_apppasswords where apaccountid not in (select accountid from hm_accounts);

delete from hm_fetchaccounts_uids where uidfaid not in (select faid from hm_fetchaccounts);

delete from hm_fetchaccounts where faaccountid not in (select accountid from hm_accounts);

delete from hm_routeaddresses where routeaddressrouteid not in (select routeid from hm_routes);

delete from hm_distributionlistsrecipients where distributionlistrecipientlistid not in (select distributionlistid from hm_distributionlists);

delete from hm_distributionlists where distributionlistdomainid not in (select domainid from hm_domains);

delete from hm_domain_aliases where dadomainid not in (select domainid from hm_domains);

delete from hm_aliases where aliasdomainid not in (select domainid from hm_domains);

delete from hm_accounts where accountdomainid not in (select domainid from hm_domains);



ALTER TABLE hm_accounts DROP FOREIGN KEY fk_hm_accounts_domain /* [IGNORE-ERRORS] */;

ALTER TABLE hm_accounts ADD CONSTRAINT fk_hm_accounts_domain FOREIGN KEY (accountdomainid) REFERENCES hm_domains (domainid) ON DELETE CASCADE;

ALTER TABLE hm_aliases DROP FOREIGN KEY fk_hm_aliases_domain /* [IGNORE-ERRORS] */;

ALTER TABLE hm_aliases ADD CONSTRAINT fk_hm_aliases_domain FOREIGN KEY (aliasdomainid) REFERENCES hm_domains (domainid) ON DELETE CASCADE;

ALTER TABLE hm_domain_aliases DROP FOREIGN KEY fk_hm_domain_aliases_domain /* [IGNORE-ERRORS] */;

ALTER TABLE hm_domain_aliases ADD CONSTRAINT fk_hm_domain_aliases_domain FOREIGN KEY (dadomainid) REFERENCES hm_domains (domainid) ON DELETE CASCADE;

ALTER TABLE hm_distributionlists DROP FOREIGN KEY fk_hm_distributionlists_domain /* [IGNORE-ERRORS] */;

ALTER TABLE hm_distributionlists ADD CONSTRAINT fk_hm_distributionlists_domain FOREIGN KEY (distributionlistdomainid) REFERENCES hm_domains (domainid) ON DELETE CASCADE;

ALTER TABLE hm_distributionlistsrecipients DROP FOREIGN KEY fk_hm_dlrecipients_list /* [IGNORE-ERRORS] */;

ALTER TABLE hm_distributionlistsrecipients ADD CONSTRAINT fk_hm_dlrecipients_list FOREIGN KEY (distributionlistrecipientlistid) REFERENCES hm_distributionlists (distributionlistid) ON DELETE CASCADE;

ALTER TABLE hm_routeaddresses DROP FOREIGN KEY fk_hm_routeaddresses_route /* [IGNORE-ERRORS] */;

ALTER TABLE hm_routeaddresses ADD CONSTRAINT fk_hm_routeaddresses_route FOREIGN KEY (routeaddressrouteid) REFERENCES hm_routes (routeid) ON DELETE CASCADE;

ALTER TABLE hm_fetchaccounts DROP FOREIGN KEY fk_hm_fetchaccounts_account /* [IGNORE-ERRORS] */;

ALTER TABLE hm_fetchaccounts ADD CONSTRAINT fk_hm_fetchaccounts_account FOREIGN KEY (faaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

ALTER TABLE hm_fetchaccounts_uids DROP FOREIGN KEY fk_hm_fetchaccounts_uids_fa /* [IGNORE-ERRORS] */;

ALTER TABLE hm_fetchaccounts_uids ADD CONSTRAINT fk_hm_fetchaccounts_uids_fa FOREIGN KEY (uidfaid) REFERENCES hm_fetchaccounts (faid) ON DELETE CASCADE;

ALTER TABLE hm_apppasswords DROP FOREIGN KEY fk_hm_apppasswords_account /* [IGNORE-ERRORS] */;

ALTER TABLE hm_apppasswords ADD CONSTRAINT fk_hm_apppasswords_account FOREIGN KEY (apaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

ALTER TABLE hm_rule_criterias DROP FOREIGN KEY fk_hm_rule_criterias_rule /* [IGNORE-ERRORS] */;

ALTER TABLE hm_rule_criterias ADD CONSTRAINT fk_hm_rule_criterias_rule FOREIGN KEY (criteriaruleid) REFERENCES hm_rules (ruleid) ON DELETE CASCADE;

ALTER TABLE hm_rule_actions DROP FOREIGN KEY fk_hm_rule_actions_rule /* [IGNORE-ERRORS] */;

ALTER TABLE hm_rule_actions ADD CONSTRAINT fk_hm_rule_actions_rule FOREIGN KEY (actionruleid) REFERENCES hm_rules (ruleid) ON DELETE CASCADE;

ALTER TABLE hm_group_members DROP FOREIGN KEY fk_hm_group_members_group /* [IGNORE-ERRORS] */;

ALTER TABLE hm_group_members ADD CONSTRAINT fk_hm_group_members_group FOREIGN KEY (membergroupid) REFERENCES hm_groups (groupid) ON DELETE CASCADE;

ALTER TABLE hm_passwordhistory DROP FOREIGN KEY fk_hm_passwordhistory_account /* [IGNORE-ERRORS] */;

ALTER TABLE hm_passwordhistory ADD CONSTRAINT fk_hm_passwordhistory_account FOREIGN KEY (phaccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

ALTER TABLE hm_messagerecipients DROP FOREIGN KEY fk_hm_messagerecipients_message /* [IGNORE-ERRORS] */;

ALTER TABLE hm_messagerecipients ADD CONSTRAINT fk_hm_messagerecipients_message FOREIGN KEY (recipientmessageid) REFERENCES hm_messages (messageid) ON DELETE CASCADE;

ALTER TABLE hm_message_metadata DROP FOREIGN KEY fk_hm_message_metadata_message /* [IGNORE-ERRORS] */;

ALTER TABLE hm_message_metadata ADD CONSTRAINT fk_hm_message_metadata_message FOREIGN KEY (metadata_messageid) REFERENCES hm_messages (messageid) ON DELETE CASCADE;

ALTER TABLE hm_imapexpunged DROP FOREIGN KEY fk_hm_imapexpunged_folder /* [IGNORE-ERRORS] */;

ALTER TABLE hm_imapexpunged ADD CONSTRAINT fk_hm_imapexpunged_folder FOREIGN KEY (expungedfolderid) REFERENCES hm_imapfolders (folderid) ON DELETE CASCADE;

ALTER TABLE hm_messageindexterms DROP FOREIGN KEY fk_hm_messageindexterms_message /* [IGNORE-ERRORS] */;

ALTER TABLE hm_messageindexterms ADD CONSTRAINT fk_hm_messageindexterms_message FOREIGN KEY (mitmessageid) REFERENCES hm_messages (messageid) ON DELETE CASCADE;

update hm_dbversion set value = 6030;
