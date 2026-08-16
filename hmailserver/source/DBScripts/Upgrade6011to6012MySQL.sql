alter table hm_rule_criterias modify criteriamatchvalue varchar(2000) not null;

alter table hm_accounts add column accountvacationbegindate datetime not null default '2001-01-01 00:00:00';

update hm_dbversion set value = 6012;
